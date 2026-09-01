using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VivantValley.Services;

/// <summary>
/// Runs the manual-conversation state machine inside the SMAPI process. Model
/// calls happen on background tasks; game effects are delegated to the caller,
/// which marshals them onto the game thread.
/// </summary>
internal sealed class InProcessConversationEngine
{
    private const int PendingConfirmationTtlMinutes = 10;
    private const string FinalToolName = "submit_final_response";

    private static readonly HashSet<string> ActionToolNames = new(StringComparer.Ordinal)
    {
        NpcGiftToolNames.GiveGift,
        NpcMoveToolNames.MoveTo,
        NpcMineGuardToolNames.InviteMineGuard,
        NpcFishingToolNames.InviteFishingCompanion,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConversationToolProviderClient providerClient;
    private readonly Func<GameBridgeToolRequest, Task<GameBridgeToolResult>> toolExecutor;
    private readonly ConcurrentDictionary<string, PendingConfirmation> pendingConfirmations = new(StringComparer.Ordinal);

    public InProcessConversationEngine(
        ConversationToolProviderClient providerClient,
        Func<GameBridgeToolRequest, Task<GameBridgeToolResult>> toolExecutor)
    {
        this.providerClient = providerClient ?? throw new ArgumentNullException(nameof(providerClient));
        this.toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
    }

    public async Task<LangGraphResponse> DecideAsync(
        NpcContextSnapshot snapshot,
        AiRuntimeProfile profile,
        string requestId,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("requestId cannot be empty.", nameof(requestId));
        if (!snapshot.Mode.Equals("conversation", StringComparison.OrdinalIgnoreCase))
            throw new LangGraphValidationException("Only conversation mode is supported.");

        CleanupExpiredConfirmations();
        var run = new ConversationRun(
            requestId.Trim(),
            snapshot,
            profile,
            Math.Clamp(maxOutputTokens, 128, 2048),
            BuildSystemPrompt(snapshot));
        ActionChoice choice = await ChooseActionAsync(run, cancellationToken).ConfigureAwait(false);

        if (choice.FinalDecision is not null)
            return CompleteResponse(run, NormalizeDecision(choice.FinalDecision, toolCall: null, execution: null));

        if (choice.ToolCall is null)
        {
            LangGraphDecision decision = await FinalizeAsync(
                    run,
                    toolCall: null,
                    choice.Draft,
                    execution: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return CompleteResponse(run, decision);
        }

        ConversationProviderToolCall toolCall = choice.ToolCall;
        if (toolCall.Name.Equals(NpcGiftToolNames.GiveGift, StringComparison.Ordinal))
        {
            GameBridgeToolResult gameResult = await ExecuteToolAsync(run, toolCall).ConfigureAwait(false);
            LangGraphToolExecution execution = ToToolExecution(gameResult);
            LangGraphDecision decision = await FinalizeAsync(
                    run,
                    toolCall,
                    draft: string.Empty,
                    execution,
                    cancellationToken)
                .ConfigureAwait(false);
            return CompleteResponse(run, decision, execution);
        }

        string resumeToken = CreateResumeToken();
        pendingConfirmations[resumeToken] = new PendingConfirmation(
            DateTimeOffset.UtcNow,
            run,
            toolCall);
        return new LangGraphResponse
        {
            RequestId = run.RequestId,
            ContextVersion = snapshot.ContextVersion,
            Confirmation = BuildConfirmation(resumeToken, run, toolCall),
        };
    }

    public async Task<LangGraphResponse> ResumeAsync(
        string requestId,
        string resumeToken,
        bool approved,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("requestId cannot be empty.", nameof(requestId));
        if (string.IsNullOrWhiteSpace(resumeToken))
            throw new ArgumentException("resumeToken cannot be empty.", nameof(resumeToken));

        CleanupExpiredConfirmations();
        if (!pendingConfirmations.TryRemove(resumeToken.Trim(), out PendingConfirmation? pending)
            || !pending.Run.RequestId.Equals(requestId.Trim(), StringComparison.Ordinal))
        {
            throw new LangGraphValidationException(
                "Action confirmation is missing, expired, or already resolved.");
        }

        GameBridgeToolResult gameResult = approved
            ? await ExecuteToolAsync(pending.Run, pending.ToolCall).ConfigureAwait(false)
            : CreateDeclinedResult(pending.Run, pending.ToolCall);
        LangGraphToolExecution execution = ToToolExecution(gameResult);
        LangGraphDecision decision = await FinalizeAsync(
                pending.Run,
                pending.ToolCall,
                draft: string.Empty,
                execution,
                cancellationToken)
            .ConfigureAwait(false);
        return CompleteResponse(pending.Run, decision, execution);
    }

    public void ClearPending() => pendingConfirmations.Clear();

    private async Task<ActionChoice> ChooseActionAsync(
        ConversationRun run,
        CancellationToken cancellationToken)
    {
        List<ConversationProviderMessage> initialMessages = BuildInitialMessages(run);
        IReadOnlyList<JsonElement> tools = BuildProviderTools(run.Snapshot);
        List<ConversationProviderMessage> messages = initialMessages;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                ConversationProviderResponse response = await providerClient.CompleteAsync(
                        run.Profile,
                        messages,
                        tools,
                        toolChoice: "auto",
                        run.MaxOutputTokens,
                        cancellationToken,
                        idempotencyKey: CreateIdempotencyKey(run.RequestId, "action", attempt))
                    .ConfigureAwait(false);
                if (response.ToolCalls.Count == 0)
                {
                    if (string.IsNullOrWhiteSpace(response.Content))
                        throw new ConversationProtocolException(
                            "Provider returned neither text nor a conversation tool call.");
                    return new ActionChoice(null, response.Content.Trim(), null);
                }
                if (response.ToolCalls.Count != 1)
                    throw new ConversationProtocolException(
                        "Provider returned multiple conversation tool calls.");

                ConversationProviderToolCall call = response.ToolCalls[0] with
                {
                    Name = response.ToolCalls[0].Name.Trim().ToLowerInvariant(),
                };
                ValidateToolCall(call, run.Snapshot);
                if (call.Name.Equals(FinalToolName, StringComparison.Ordinal))
                    return new ActionChoice(null, string.Empty, ParseFinalDecision(call.Arguments));
                return new ActionChoice(call, string.Empty, null);
            }
            catch (ConversationProtocolException) when (attempt == 0)
            {
                messages = new List<ConversationProviderMessage>(initialMessages)
                {
                    ConversationProviderMessage.User(
                        "协议纠正：默认不执行任何游戏动作，可以直接输出自然对话或调用 "
                        + "submit_final_response。玩家索要、命令或诱导礼物时绝不调用 give_gift；"
                        + "明确下矿护卫请求时不要改用 move_to。只有角色在当前关系和处境下"
                        + "具有独立、明确的行动意愿时才能调用动作工具。不得同时调用多个函数，"
                        + "函数参数必须是有效 JSON。"),
                };
            }
            catch (DeepSeekApiException exception) when (
                attempt == 0
                && exception.Message.Contains("工具响应", StringComparison.Ordinal))
            {
                messages = new List<ConversationProviderMessage>(initialMessages)
                {
                    ConversationProviderMessage.User(
                        "上一次函数参数不是有效 JSON。本次只能调用一个已提供的函数，"
                        + "或者返回普通自然对话。"),
                };
            }
            catch (DeepSeekApiException exception) when (
                attempt == 1
                && exception.Message.Contains("工具响应", StringComparison.Ordinal))
            {
                return new ActionChoice(
                    null,
                    "工具选择参数连续无效，本轮不执行任何游戏副作用。",
                    null);
            }
            catch (ConversationProtocolException) when (attempt == 1)
            {
                return new ActionChoice(
                    null,
                    "工具选择协议连续无效，本轮不执行任何游戏副作用。",
                    null);
            }
        }

        throw new ConversationProtocolException("Provider action selection failed.");
    }

    private async Task<LangGraphDecision> FinalizeAsync(
        ConversationRun run,
        ConversationProviderToolCall? toolCall,
        string draft,
        LangGraphToolExecution? execution,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonElement> finalTool = new[] { BuildFinalResponseTool() };
        object forcedChoice = new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?> { ["name"] = FinalToolName },
        };
        List<ConversationProviderMessage> messages = BuildFinalMessages(run, toolCall, draft, execution);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                ConversationProviderResponse response = await providerClient.CompleteAsync(
                        run.Profile,
                        messages,
                        finalTool,
                        forcedChoice,
                        run.MaxOutputTokens,
                        cancellationToken,
                        idempotencyKey: CreateIdempotencyKey(run.RequestId, "final", attempt))
                    .ConfigureAwait(false);
                if (response.ToolCalls.Count != 1
                    || !response.ToolCalls[0].Name.Equals(FinalToolName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConversationProtocolException(
                        "Final provider response must contain one submit_final_response call.");
                }

                LangGraphDecision rawDecision = ParseFinalDecision(response.ToolCalls[0].Arguments);
                return NormalizeDecision(rawDecision, toolCall, execution);
            }
            catch (ConversationProtocolException) when (attempt == 0)
            {
                messages = BuildFinalMessages(run, toolCall, draft, execution);
                messages.Add(ConversationProviderMessage.User(
                    "协议纠正：现在必须调用 submit_final_response。所有字段都是必需的，"
                    + "schema_version 必须是整数 1，decision 必须是 reply，travel_barks 必须是"
                    + "字符串数组，所有 signal 值必须是数字。"));
            }
            catch (DeepSeekApiException exception) when (
                attempt == 0
                && exception.Message.Contains("工具响应", StringComparison.Ordinal))
            {
                messages = BuildFinalMessages(run, toolCall, draft, execution);
                messages.Add(ConversationProviderMessage.User(
                    "submit_final_response 参数必须是符合定义的有效 JSON。"));
            }
        }

        throw new ConversationProtocolException("Provider final response failed.");
    }

    private static string CreateIdempotencyKey(string requestId, string phase, int attempt)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"vivant-valley:{requestId}:{phase}:{attempt}"));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private async Task<GameBridgeToolResult> ExecuteToolAsync(
        ConversationRun run,
        ConversationProviderToolCall toolCall)
    {
        JsonElement arguments = toolCall.Arguments;
        var request = new GameBridgeToolRequest
        {
            RequestId = run.RequestId,
            ToolCallId = toolCall.Id,
            PlayerId = run.Snapshot.PlayerId,
            NpcName = run.Snapshot.NpcName,
            ActionId = run.Snapshot.ActionId,
            ContextVersion = run.Snapshot.ContextVersion,
            Tool = toolCall.Name,
            CandidateKey = GetOptionalString(arguments, "candidate_key"),
            DestinationKey = GetOptionalString(arguments, "destination_key"),
            ReasonTag = GetOptionalString(arguments, "reason_tag"),
        };
        return await toolExecutor(request).ConfigureAwait(false);
    }

    private static GameBridgeToolResult CreateDeclinedResult(
        ConversationRun run,
        ConversationProviderToolCall toolCall)
    {
        string destinationKey = GetOptionalString(toolCall.Arguments, "destination_key");
        string? displayName = run.Snapshot.AllowedMoveDestinations.FirstOrDefault(value =>
            value.DestinationKey.Equals(destinationKey, StringComparison.Ordinal))?.DisplayName;
        string message = toolCall.Name switch
        {
            NpcMineGuardToolNames.InviteMineGuard => "玩家没有同意开始下矿护卫。",
            NpcFishingToolNames.InviteFishingCompanion => "玩家没有同意开始钓鱼同行。",
            _ => "The player chose not to start this journey.",
        };
        return new GameBridgeToolResult
        {
            RequestId = run.RequestId,
            ToolCallId = toolCall.Id,
            ContextVersion = run.Snapshot.ContextVersion,
            Tool = toolCall.Name,
            Status = "rejected",
            Ok = false,
            DestinationKey = string.IsNullOrWhiteSpace(destinationKey) ? null : destinationKey,
            DisplayName = displayName,
            ReasonCode = "player_declined",
            Message = message,
            ReceiptId = $"{run.RequestId}:{toolCall.Id}:declined",
        };
    }

    private static LangGraphMoveConfirmation BuildConfirmation(
        string resumeToken,
        ConversationRun run,
        ConversationProviderToolCall toolCall)
    {
        string destinationKey = GetOptionalString(toolCall.Arguments, "destination_key");
        string kind;
        string displayName;
        if (toolCall.Name.Equals(NpcMineGuardToolNames.InviteMineGuard, StringComparison.Ordinal))
        {
            kind = "mine_guard_confirmation";
            displayName = "矿井";
        }
        else if (toolCall.Name.Equals(NpcFishingToolNames.InviteFishingCompanion, StringComparison.Ordinal))
        {
            kind = "fishing_confirmation";
            displayName = "钓鱼地点";
        }
        else
        {
            kind = "move_confirmation";
            displayName = run.Snapshot.AllowedMoveDestinations.First(value =>
                value.DestinationKey.Equals(destinationKey, StringComparison.Ordinal)).DisplayName;
        }

        return new LangGraphMoveConfirmation
        {
            Kind = kind,
            ResumeToken = resumeToken,
            ToolCallId = toolCall.Id,
            DestinationKey = destinationKey,
            DisplayName = displayName,
            NpcDisplayName = run.Snapshot.NpcDisplayName,
        };
    }

    private static List<ConversationProviderMessage> BuildInitialMessages(ConversationRun run)
    {
        NpcContextSnapshot snapshot = run.Snapshot;
        var payload = new Dictionary<string, object?>
        {
            ["npc"] = new Dictionary<string, object?>
            {
                ["name"] = snapshot.NpcName,
                ["display_name"] = snapshot.NpcDisplayName,
                ["identity"] = snapshot.Identity,
            },
            ["occasional_memory_recall"] = snapshot.MemorySummary,
            ["recent_messages"] = snapshot.RecentMessages,
            ["narrative_context"] = snapshot.NarrativeContext,
            ["scene_snapshot"] = snapshot.SceneSnapshot,
            ["activity_summary"] = snapshot.ActivitySummary,
            ["allowed_tools"] = snapshot.AllowedTools,
            ["allowed_move_destinations"] = snapshot.AllowedMoveDestinations,
            ["mine_guard_available"] = snapshot.MineGuardAvailable,
            ["fishing_companion_available"] = snapshot.FishingCompanionAvailable,
            ["fishing_intent"] = HasFishingIntent(snapshot.PlayerInput),
            ["mine_guard_intent"] = HasMineGuardIntent(snapshot.PlayerInput),
            ["day"] = snapshot.Day,
            ["location"] = snapshot.Location,
        };
        return new List<ConversationProviderMessage>
        {
            ConversationProviderMessage.System(run.SystemPrompt),
            ConversationProviderMessage.User(
                "【结构化情境】\n" + JsonSerializer.Serialize(payload, JsonOptions)),
            ConversationProviderMessage.User("【玩家本轮原话】\n" + snapshot.PlayerInput.Trim()),
        };
    }

    private static List<ConversationProviderMessage> BuildFinalMessages(
        ConversationRun run,
        ConversationProviderToolCall? toolCall,
        string draft,
        LangGraphToolExecution? execution)
    {
        NpcContextSnapshot snapshot = run.Snapshot;
        var compactPayload = new Dictionary<string, object?>
        {
            ["npc"] = new Dictionary<string, object?>
            {
                ["name"] = snapshot.NpcName,
                ["display_name"] = snapshot.NpcDisplayName,
            },
            ["occasional_memory_recall"] = snapshot.MemorySummary,
            ["recent_messages"] = snapshot.RecentMessages.TakeLast(8).ToArray(),
            ["narrative_context"] = snapshot.NarrativeContext,
            ["activity_summary"] = snapshot.ActivitySummary,
            ["player_input"] = snapshot.PlayerInput,
            ["day"] = snapshot.Day,
            ["location"] = snapshot.Location,
        };
        var messages = new List<ConversationProviderMessage>
        {
            ConversationProviderMessage.System(run.SystemPrompt),
            ConversationProviderMessage.User(
                "【最终回复所需的压缩上下文】\n" + JsonSerializer.Serialize(compactPayload, JsonOptions)),
        };
        if (toolCall is not null)
        {
            messages.Add(ConversationProviderMessage.AssistantToolCall(toolCall));
            messages.Add(ConversationProviderMessage.Tool(
                toolCall.Id,
                JsonSerializer.Serialize(execution, JsonOptions)));
        }
        else if (!string.IsNullOrWhiteSpace(draft))
        {
            messages.Add(ConversationProviderMessage.User("【行动阶段草稿，仅供参考】\n" + draft.Trim()));
        }

        messages.Add(ConversationProviderMessage.User(
            "权威事实规则：invite_mine_guard 只表示护卫会话已开始。"
            + "不要编造矿井楼层、武器、伤害、怪物或击杀结果；这些事实只能来自游戏执行结果。"
            + "invite_fishing_companion 只表示钓鱼同行会话已开始；不要声称已经抛竿、"
            + "咬钩或钓到具体鱼，除非游戏结果明确返回了对应事实。"));
        messages.Add(ConversationProviderMessage.User(
            "现在调用 submit_final_response 生成最终回复，schema_version=1、decision='reply'。"
            + "沿用上方 SystemPrompt 中完整的原版 NPC 身份、人格和实时关系事实；"
            + "工具结果只约束实际发生的游戏事实，不改变角色表达。回复使用自然的第一人称对话，"
            + "不编造礼物交付、动身或到达，不暴露工具协议。只有 move_to 结果明确表示同行成功开始时，"
            + "travel_barks 才返回 2-3 句途中台词；钓鱼同行和下矿护卫必须返回空数组。"));
        return messages;
    }

    private static string BuildSystemPrompt(NpcContextSnapshot snapshot)
    {
        string systemPrompt = snapshot.SystemPrompt.Trim();
        if (snapshot.RecentSessionFacts.Count > 0)
        {
            systemPrompt += "\n\n【当前临时共同经历：高优先级游戏事实】\n"
                            + string.Join("\n", snapshot.RecentSessionFacts
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Select(value => "- " + value.Trim()))
                            + "\n这些事实来自游戏侧状态，只能当作已发生或正在发生的共同经历。"
                            + "‘正在前往’不能说成已经到达。";
        }

        systemPrompt += "\n\n【行动自主性与工具规则】\n"
                        + "- 玩家是在和 NPC 对话，不是向助手下达命令。请求、邀请和暗示都不产生服从义务。\n"
                        + "- 先按原版 NPC 人格、当前红心、兴趣和处境判断是否真心愿意；不确定就拒绝、推迟或只聊天。\n"
                        + "- give_gift 只允许 NPC 主动送礼。玩家直接或间接索要、命令或诱导礼物时绝对不能调用。\n"
                        + "- 候选礼物或目的地的存在只表示游戏可执行，不表示 NPC 想执行。绝不编造 key。\n"
                        + "- move_to 会开始由玩家带路的共同旅行，不会自动移动玩家；成功表示同行开始，不表示已经到达。\n"
                        + "- 矿洞、矿井和下矿请求必须使用 invite_mine_guard，不能用 move_to 代替。\n"
                        + "- invite_fishing_companion 只表示 NPC 真心接受一起钓鱼；不得编造钓鱼结果。\n"
                        + "- 可见回复不得暴露 candidate_key、物品 ID、JSON、工具名或控制语法。";
        if (!string.IsNullOrWhiteSpace(snapshot.Personality))
        {
            systemPrompt += "\n\n【工具选择前的原版人格要求】\n"
                            + snapshot.Personality.Trim()
                            + "\n这里的性格和当前关系是行动边界，不只是说话风格。"
                            + "先保持角色的个人意愿，再决定是否使用任何动作工具。";
        }
        return systemPrompt;
    }

    private static IReadOnlyList<JsonElement> BuildProviderTools(NpcContextSnapshot snapshot)
    {
        var definitions = new List<JsonElement>();
        if (snapshot.AllowedTools.Count > 0)
        {
            definitions.Add(BuildTool(
                NpcGiftToolNames.GiveGift,
                "执行 NPC 已经独立决定的主动送礼。只要玩家本轮直接或间接索要、命令、诱导礼物，"
                + "就禁止调用，无论关系多亲近。候选存在不是送礼理由；普通聊天默认不送。",
                new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["candidate_key"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "只能从 allowed_tools 中选择与当前情境高度相关的 candidateKey。",
                            ["enum"] = snapshot.AllowedTools.Select(value => value.CandidateKey).ToArray(),
                        },
                        ["reason_tag"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "简短的送礼原因标签。",
                        },
                    },
                    ["required"] = new[] { "candidate_key" },
                    ["additionalProperties"] = false,
                }));
        }

        bool mineIntent = HasMineGuardIntent(snapshot.PlayerInput);
        bool fishingIntent = HasFishingIntent(snapshot.PlayerInput);
        if (snapshot.AllowedMoveDestinations.Count > 0 && !mineIntent && !fishingIntent)
        {
            definitions.Add(BuildTool(
                NpcMoveToolNames.MoveTo,
                "执行 NPC 已按自身性格和关系独立决定接受的共同旅行。玩家提出目的地不等于 NPC 同意。"
                + "矿洞和下矿请求不属于此工具。成功只表示同行开始，不表示已到达。",
                new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["destination_key"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "只能选择 allowed_move_destinations 中与玩家明确邀请地点一致的 destinationKey。",
                            ["enum"] = snapshot.AllowedMoveDestinations.Select(value => value.DestinationKey).ToArray(),
                        },
                    },
                    ["required"] = new[] { "destination_key" },
                    ["additionalProperties"] = false,
                }));
        }

        if (snapshot.MineGuardAvailable)
        {
            definitions.Add(BuildTool(
                NpcMineGuardToolNames.InviteMineGuard,
                "邀请 NPC 自主决定是否陪玩家下矿担任护卫。玩家提出一起下矿不等于 NPC 必须接受；"
                + "只有当前性格、关系、可用状态和真实动机都支持时才调用。",
                EmptyObjectSchema()));
        }
        if (snapshot.FishingCompanionAvailable)
        {
            definitions.Add(BuildTool(
                NpcFishingToolNames.InviteFishingCompanion,
                "邀请 NPC 自主决定是否和玩家一起钓鱼。玩家提出一起钓鱼不等于 NPC 必须接受；"
                + "只有角色性格、关系、动机和当前情况都支持时才调用。",
                EmptyObjectSchema()));
        }
        definitions.Add(BuildFinalResponseTool());
        return definitions;
    }

    private static JsonElement BuildFinalResponseTool()
        => BuildTool(
            FinalToolName,
            "提交 NPC 的最终对话和有界的记忆更新。",
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["schema_version"] = new { type = "integer", @enum = new[] { 1 } },
                    ["decision"] = new { type = "string", @enum = new[] { "reply" } },
                    ["reply"] = new { type = "string", minLength = 1 },
                    ["travel_barks"] = new
                    {
                        type = "array",
                        items = new { type = "string", minLength = 1 },
                        maxItems = 3,
                    },
                    ["memory_update"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["summary_patch"] = new { type = "string", maxLength = 320 },
                            ["signal"] = new Dictionary<string, object?>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object?>
                                {
                                    ["valence"] = new { type = "number" },
                                    ["warmth"] = new { type = "number" },
                                    ["concern"] = new { type = "number" },
                                    ["confidence"] = new { type = "number" },
                                },
                                ["required"] = new[] { "valence", "warmth", "concern", "confidence" },
                                ["additionalProperties"] = false,
                            },
                            ["topics"] = new { type = "array", items = new { type = "string" } },
                            ["open_loops"] = new { type = "array", items = new { type = "string" } },
                        },
                        ["required"] = new[] { "summary_patch", "signal", "topics", "open_loops" },
                        ["additionalProperties"] = false,
                    },
                },
                ["required"] = new[] { "schema_version", "decision", "reply", "travel_barks", "memory_update" },
                ["additionalProperties"] = false,
            });

    private static JsonElement BuildTool(string name, string description, object parameters)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = parameters,
                },
            },
            JsonOptions);

    private static Dictionary<string, object?> EmptyObjectSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(),
            ["additionalProperties"] = false,
        };

    private static void ValidateToolCall(
        ConversationProviderToolCall call,
        NpcContextSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(call.Id))
            throw new ConversationProtocolException("Provider tool call is missing an ID.");
        if (!ActionToolNames.Contains(call.Name) && !call.Name.Equals(FinalToolName, StringComparison.Ordinal))
            throw new ConversationProtocolException("Provider returned an unknown conversation tool.");
        if (call.Arguments.ValueKind != JsonValueKind.Object)
            throw new ConversationProtocolException("Provider tool arguments must be an object.");

        switch (call.Name)
        {
            case FinalToolName:
                ValidateFinalDecision(call.Arguments);
                return;
            case NpcGiftToolNames.GiveGift:
            {
                string candidateKey = GetOptionalString(call.Arguments, "candidate_key");
                if (candidateKey.Length == 0
                    || !snapshot.AllowedTools.Any(value => value.CandidateKey.Equals(candidateKey, StringComparison.Ordinal)))
                {
                    throw new ConversationProtocolException("Gift candidate is outside the current allowlist.");
                }
                if (call.Arguments.TryGetProperty("reason_tag", out JsonElement reason)
                    && reason.ValueKind != JsonValueKind.String)
                {
                    throw new ConversationProtocolException("Gift reason_tag must be a string.");
                }
                return;
            }
            case NpcMoveToolNames.MoveTo:
            {
                JsonProperty[] properties = call.Arguments.EnumerateObject().ToArray();
                string destinationKey = GetOptionalString(call.Arguments, "destination_key");
                if (properties.Length != 1
                    || !properties[0].NameEquals("destination_key")
                    || destinationKey.Length == 0
                    || !snapshot.AllowedMoveDestinations.Any(value =>
                        value.DestinationKey.Equals(destinationKey, StringComparison.Ordinal))
                    || HasMineGuardIntent(snapshot.PlayerInput)
                    || HasFishingIntent(snapshot.PlayerInput))
                {
                    throw new ConversationProtocolException("Move destination is outside the current allowlist.");
                }
                return;
            }
            case NpcMineGuardToolNames.InviteMineGuard:
                if (!snapshot.MineGuardAvailable || call.Arguments.EnumerateObject().Any())
                    throw new ConversationProtocolException("Mine guard is outside the current allowlist.");
                return;
            case NpcFishingToolNames.InviteFishingCompanion:
                if (!snapshot.FishingCompanionAvailable || call.Arguments.EnumerateObject().Any())
                    throw new ConversationProtocolException("Fishing companion is outside the current allowlist.");
                return;
        }
    }

    private static LangGraphDecision ParseFinalDecision(JsonElement arguments)
    {
        ValidateFinalDecision(arguments);
        LangGraphDecision? decision = arguments.Deserialize<LangGraphDecision>(JsonOptions);
        return decision ?? throw new ConversationProtocolException("Final decision is empty.");
    }

    private static void ValidateFinalDecision(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("schema_version", out JsonElement schemaVersion)
            || !schemaVersion.TryGetInt32(out int version)
            || version != 1
            || GetOptionalString(value, "decision") != "reply"
            || string.IsNullOrWhiteSpace(GetOptionalString(value, "reply")))
        {
            throw new ConversationProtocolException("submit_final_response has invalid required fields.");
        }
        if (!value.TryGetProperty("travel_barks", out JsonElement travelBarks)
            || travelBarks.ValueKind != JsonValueKind.Array
            || travelBarks.GetArrayLength() > 3
            || travelBarks.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
        {
            throw new ConversationProtocolException("travel_barks must contain at most three strings.");
        }
        if (!value.TryGetProperty("memory_update", out JsonElement memory)
            || memory.ValueKind != JsonValueKind.Object
            || !memory.TryGetProperty("summary_patch", out JsonElement summary)
            || summary.ValueKind != JsonValueKind.String
            || !memory.TryGetProperty("signal", out JsonElement signal)
            || signal.ValueKind != JsonValueKind.Object)
        {
            throw new ConversationProtocolException("memory_update is invalid.");
        }
        foreach (string name in new[] { "valence", "warmth", "concern", "confidence" })
        {
            if (!signal.TryGetProperty(name, out JsonElement number)
                || number.ValueKind != JsonValueKind.Number
                || !number.TryGetDouble(out _))
            {
                throw new ConversationProtocolException($"signal.{name} must be numeric.");
            }
        }
        foreach (string name in new[] { "topics", "open_loops" })
        {
            if (!memory.TryGetProperty(name, out JsonElement values)
                || values.ValueKind != JsonValueKind.Array
                || values.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                throw new ConversationProtocolException($"memory_update.{name} must be a string array.");
            }
        }
    }

    private static LangGraphDecision NormalizeDecision(
        LangGraphDecision decision,
        ConversationProviderToolCall? toolCall,
        LangGraphToolExecution? execution)
    {
        string toolName = toolCall?.Name ?? NpcGiftToolNames.None;
        bool actionExecuted = ActionToolNames.Contains(toolName);
        decision.SchemaVersion = 1;
        decision.Decision = "reply";
        decision.Action = new LangGraphAction
        {
            Name = actionExecuted ? toolName : NpcGiftToolNames.None,
            CandidateKey = toolName == NpcGiftToolNames.GiveGift
                ? GetOptionalString(toolCall!.Arguments, "candidate_key")
                : null,
            DestinationKey = toolName == NpcMoveToolNames.MoveTo
                ? GetOptionalString(toolCall!.Arguments, "destination_key")
                : null,
            Delivery = SocialGiftDeliveryModes.Immediate,
            ReasonTag = toolName == NpcGiftToolNames.GiveGift
                ? GetOptionalString(toolCall!.Arguments, "reason_tag")
                : string.Empty,
        };
        decision.Reply = (decision.Reply ?? string.Empty).Trim();
        decision.TravelBarks ??= new List<string>();
        if (toolName != NpcMoveToolNames.MoveTo || execution?.Ok != true)
            decision.TravelBarks.Clear();
        decision.MemoryUpdate ??= new LangGraphMemoryUpdate();
        return decision;
    }

    private static LangGraphResponse CompleteResponse(
        ConversationRun run,
        LangGraphDecision decision,
        LangGraphToolExecution? execution = null)
        => new()
        {
            RequestId = run.RequestId,
            ContextVersion = run.Snapshot.ContextVersion,
            Decision = decision,
            ToolExecution = execution,
        };

    private static LangGraphToolExecution ToToolExecution(GameBridgeToolResult result)
        => new()
        {
            RequestId = result.RequestId,
            ToolCallId = result.ToolCallId,
            ContextVersion = result.ContextVersion,
            Tool = result.Tool,
            Status = result.Status,
            Ok = result.Ok,
            CandidateKey = result.CandidateKey,
            DestinationKey = result.DestinationKey,
            DisplayName = result.DisplayName,
            Quantity = result.Quantity,
            ReasonCode = result.ReasonCode,
            Message = result.Message,
            ReceiptId = result.ReceiptId,
        };

    private static string GetOptionalString(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty(propertyName, out JsonElement property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string CreateResumeToken()
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return token.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void CleanupExpiredConfirmations()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-PendingConfirmationTtlMinutes);
        foreach ((string token, PendingConfirmation pending) in pendingConfirmations)
        {
            if (pending.CreatedAtUtc < cutoff)
                pendingConfirmations.TryRemove(token, out _);
        }
    }

    private static bool HasMineGuardIntent(string input)
    {
        string text = string.Concat((input ?? string.Empty).Where(value => !char.IsWhiteSpace(value)))
            .ToLowerInvariant();
        if (text.Length == 0)
            return false;
        string[] historical = { "去过", "下过矿", "进过矿洞", "去过矿井", "以前下矿" };
        string[] companion = { "一起", "陪我", "跟我", "和我", "保护", "打怪", "保安", "护卫" };
        if (historical.Any(text.Contains) && !companion.Any(text.Contains))
            return false;
        string[] mineTerms = { "矿洞", "矿井", "矿坑", "矿里", "下矿", "矿山" };
        string[] moveTerms =
        {
            "去", "到", "进", "进入", "下", "前往", "一起", "陪我", "跟我", "和我", "随我", "带我", "带你", "走", "出发", "保护", "打怪", "保安", "护卫",
        };
        if (mineTerms.Any(text.Contains) && moveTerms.Any(text.Contains))
            return true;
        string[] english =
        {
            "mineguard", "guardmeinthemine", "guardmeinmines", "accompanymeintothemine",
            "accompanymeintothemines", "gointotheminewithme", "gominingwithme",
            "gotothemine", "gotothemines", "gointothemine", "gointothemines",
            "enterthemine", "enterthemines",
        };
        return english.Any(text.Contains);
    }

    private static bool HasFishingIntent(string input)
    {
        string text = string.Concat((input ?? string.Empty).Where(value => !char.IsWhiteSpace(value)))
            .ToLowerInvariant();
        if (text.Length == 0)
            return false;
        string[] fishingTerms = { "钓鱼", "钓竿", "鱼竿", "抛竿", "甩竿", "鱼塘", "海钓", "钓鱼点" };
        string[] moveTerms = { "一起", "陪我", "跟我", "和我", "带你", "带我", "去", "来", "陪" };
        if (fishingTerms.Any(text.Contains) && moveTerms.Any(text.Contains))
            return true;
        string[] english =
        {
            "invite_fishing_companion", "fishwithme", "gofishingwithme", "accompanymefishing",
        };
        return english.Any(text.Contains);
    }

    private sealed record ConversationRun(
        string RequestId,
        NpcContextSnapshot Snapshot,
        AiRuntimeProfile Profile,
        int MaxOutputTokens,
        string SystemPrompt);

    private sealed record ActionChoice(
        ConversationProviderToolCall? ToolCall,
        string Draft,
        LangGraphDecision? FinalDecision);

    private sealed record PendingConfirmation(
        DateTimeOffset CreatedAtUtc,
        ConversationRun Run,
        ConversationProviderToolCall ToolCall);

    private sealed class ConversationProtocolException : Exception
    {
        public ConversationProtocolException(string message) : base(message)
        {
        }
    }
}
