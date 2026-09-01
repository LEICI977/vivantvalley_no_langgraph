using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace VivantValley.Menus;

/// <summary>In-game account login/register for the Vivant Valley hosted service.</summary>
public sealed class HostedAccountMenu : IClickableMenu
{
    private readonly Func<string, string, bool, Task<(bool Success, string Message, string Token, string Model, long BalanceMicros)>> authenticate;
    private readonly Action<string, string, long> onSuccess;
    private readonly Func<string, string, Task<string>>? redeem;
    private readonly Func<string, Task<long>>? refreshBalance;
    private readonly Action? onOpenDirectSettings;
    private readonly TextBox emailBox;
    private readonly TextBox passwordBox;
    private readonly TextBox modelBox;
    private readonly TextBox redeemBox;
    private readonly ClickableComponent loginButton = new(Rectangle.Empty, "login");
    private readonly ClickableComponent registerButton = new(Rectangle.Empty, "register");
    private readonly ClickableComponent directSettingsButton = new(Rectangle.Empty, "direct-settings");
    private readonly ClickableComponent redeemButton = new(Rectangle.Empty, "redeem");
    private readonly Dictionary<int, ClickableComponent> purchaseButtons = new()
    {
        [1] = new ClickableComponent(Rectangle.Empty, "purchase-1"),
        [5] = new ClickableComponent(Rectangle.Empty, "purchase-5"),
        [10] = new ClickableComponent(Rectangle.Empty, "purchase-10"),
        [20] = new ClickableComponent(Rectangle.Empty, "purchase-20"),
    };
    private static readonly IReadOnlyDictionary<int, string> PurchaseUrls = new Dictionary<int, string>
    {
        [1] = "https://pay.ldxp.cn/item/jxtjmi",
        [5] = "https://pay.ldxp.cn/item/eip1kt",
        [10] = "https://pay.ldxp.cn/item/mus6ix",
        [20] = "https://pay.ldxp.cn/item/mpk1fb",
    };
    private Task<(bool Success, string Message, string Token, string Model, long BalanceMicros)>? pending;
    private Task<string>? pendingRedeem;
    private Task<long>? pendingBalance;
    private string authToken;
    private long balanceMicros = -1;
    private bool closed;
    private string status = "登录后即可使用托管模型并按账户额度结算。";
    private Color statusColor = Game1.textColor;

    public HostedAccountMenu(
        Func<string, string, bool, Task<(bool Success, string Message, string Token, string Model, long BalanceMicros)>> authenticate,
        Action<string, string, long> onSuccess,
        Func<string, string, Task<string>>? redeem = null,
        Action? onCancel = null,
        Action? onOpenDirectSettings = null,
        Func<string, Task<long>>? refreshBalance = null,
        string? initialToken = null,
        string? initialModel = null)
    {
        this.authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate));
        this.onSuccess = onSuccess ?? throw new ArgumentNullException(nameof(onSuccess));
        this.redeem = redeem;
        this.refreshBalance = refreshBalance;
        this.onOpenDirectSettings = onOpenDirectSettings;
        authToken = initialToken?.Trim() ?? string.Empty;
        Texture2D texture = Game1.content.Load<Texture2D>("LooseSprites\\textBox");
        emailBox = CreateTextBox(texture, 320, false);
        passwordBox = CreateTextBox(texture, 320, true);
        modelBox = CreateTextBox(texture, 80, false);
        modelBox.Text = string.IsNullOrWhiteSpace(initialModel) ? "vv-dialogue" : initialModel.Trim();
        redeemBox = CreateTextBox(texture, 128, false);
        width = 760;
        height = 620;
        Reposition();
        initializeUpperRightCloseButton();
        Select(emailBox);
        if (authToken.Length > 0 && refreshBalance is not null)
            pendingBalance = refreshBalance(authToken);
    }

    public override void update(GameTime time)
    {
        base.update(time);
        if (pending is not null && pending.IsCompleted)
        {
            var task = pending; pending = null;
            try
            {
                var result = task.GetAwaiter().GetResult();
                if (result.Success)
                {
                    authToken = result.Token;
                    balanceMicros = result.BalanceMicros;
                    onSuccess(result.Token, string.IsNullOrWhiteSpace(modelBox.Text) ? result.Model : modelBox.Text.Trim(), balanceMicros);
                    status = "登录成功。可选择模型；兑换码可直接充值。";
                    statusColor = Color.DarkGreen;
                }
                else { status = result.Message; statusColor = Color.DarkRed; }
            }
            catch (Exception ex) { status = ex.Message; statusColor = Color.DarkRed; }
        }

        if (pendingRedeem is not null && pendingRedeem.IsCompleted)
        {
            try { status = pendingRedeem.GetAwaiter().GetResult(); statusColor = Color.DarkGreen; }
            catch (Exception ex) { status = ex.Message; statusColor = Color.DarkRed; }
            pendingRedeem = null;
            StartBalanceRefresh();
        }

        if (pendingBalance is not null && pendingBalance.IsCompleted)
        {
            Task<long> task = pendingBalance;
            pendingBalance = null;
            try
            {
                balanceMicros = task.GetAwaiter().GetResult();
            }
            catch
            {
                status = "无法读取托管余额，请重新登录。";
                statusColor = Color.DarkRed;
            }
        }
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        Reposition();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (upperRightCloseButton?.containsPoint(x, y) == true) { Close(); return; }
        if (loginButton.containsPoint(x, y)) { Start(false); return; }
        if (registerButton.containsPoint(x, y)) { Start(true); return; }
        if (directSettingsButton.containsPoint(x, y)) { OpenDirectSettings(); return; }
        if (redeemButton.containsPoint(x, y) && authToken.Length > 0) { StartRedeem(); return; }
        foreach ((int denomination, ClickableComponent button) in purchaseButtons)
        {
            if (button.containsPoint(x, y))
            {
                OpenPurchasePage(denomination);
                return;
            }
        }
        if (Contains(emailBox, x, y)) Select(emailBox);
        else if (Contains(passwordBox, x, y)) Select(passwordBox);
        else if (Contains(modelBox, x, y)) Select(modelBox);
        else if (Contains(redeemBox, x, y)) Select(redeemBox);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape) { Close(); return; }
        if (key == Keys.Tab)
        {
            Select(ReferenceEquals(Game1.keyboardDispatcher.Subscriber, emailBox) ? passwordBox : ReferenceEquals(Game1.keyboardDispatcher.Subscriber, passwordBox) ? modelBox : ReferenceEquals(Game1.keyboardDispatcher.Subscriber, modelBox) ? redeemBox : emailBox);
            return;
        }
        if (!emailBox.Selected && !passwordBox.Selected && !modelBox.Selected && !redeemBox.Selected) base.receiveKeyPress(key);
    }

    public override void performHoverAction(int x, int y)
    {
        emailBox.Hover(x, y); passwordBox.Hover(x, y); modelBox.Hover(x, y); redeemBox.Hover(x, y);
        loginButton.scale = loginButton.containsPoint(x, y) ? 1.03f : 1f;
        registerButton.scale = registerButton.containsPoint(x, y) ? 1.03f : 1f;
        directSettingsButton.scale = directSettingsButton.containsPoint(x, y) ? 1.03f : 1f;
        redeemButton.scale = redeemButton.containsPoint(x, y) ? 1.03f : 1f;
        foreach (ClickableComponent button in purchaseButtons.Values)
            button.scale = button.containsPoint(x, y) ? 1.03f : 1f;
        upperRightCloseButton?.tryHover(x, y, 0.2f);
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.45f);
        drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
        b.DrawString(Game1.dialogueFont, "Vivant Valley 托管账户", new Vector2(xPositionOnScreen + 36, yPositionOnScreen + 24), Game1.textColor);
        b.DrawString(Game1.smallFont, "计费：每 1,000,000 Token ¥1.00；购买后获得卡密并在此兑换。", new Vector2(xPositionOnScreen + 36, yPositionOnScreen + 58), Game1.textColor);
        b.DrawString(Game1.smallFont, "邮箱", new Vector2(emailBox.X, emailBox.Y - 26), Game1.textColor);
        b.DrawString(Game1.smallFont, "密码（至少 12 位，建议包含大小写字母、数字和符号）", new Vector2(passwordBox.X, passwordBox.Y - 26), Game1.textColor);
        b.DrawString(Game1.smallFont, "模型别名（默认 vv-dialogue，可填 vv-fast）", new Vector2(modelBox.X, modelBox.Y - 26), Game1.textColor);
        emailBox.Draw(b, false); passwordBox.Draw(b, false); modelBox.Draw(b, false);
        b.DrawString(Game1.smallFont, "兑换码（登录后可选）", new Vector2(redeemBox.X, redeemBox.Y - 26), Game1.textColor);
        redeemBox.Draw(b, false);
        DrawButton(b, redeemButton, "兑换额度", pendingRedeem is not null || authToken.Length == 0);
        b.DrawString(Game1.smallFont, FormatBalance(), new Vector2(emailBox.X, redeemBox.Y + 54), Game1.textColor);
        b.DrawString(Game1.smallFont, "购买后获得卡密，再粘贴到上方兑换。", new Vector2(emailBox.X, redeemBox.Y + 78), Game1.textColor);
        foreach ((int denomination, ClickableComponent button) in purchaseButtons)
            DrawButton(b, button, $"购买 ¥{denomination}", false);
        DrawButton(b, loginButton, "登录", pending is not null);
        DrawButton(b, registerButton, "注册并登录", pending is not null);
        DrawButton(b, directSettingsButton, "DeepSeek / OpenAI", false);
        b.DrawString(Game1.smallFont, status, new Vector2(xPositionOnScreen + 36, loginButton.bounds.Y - 30), statusColor);
        upperRightCloseButton?.draw(b); drawMouse(b);
    }

    private void Start(bool register)
    {
        if (pending is not null) return;
        string email = (emailBox.Text ?? string.Empty).Trim(); string password = passwordBox.Text ?? string.Empty;
        if (email.Length == 0 || password.Length < 12) { status = "请输入有效邮箱和至少 12 位密码。"; statusColor = Color.DarkRed; return; }
        status = register ? "正在创建账户…" : "正在登录…"; statusColor = Color.Gray;
        pending = authenticate(email, password, register);
    }

    private void StartRedeem()
    {
        if (pendingRedeem is not null || redeem is null) return;
        string code = (redeemBox.Text ?? string.Empty).Trim();
        if (code.Length == 0) { status = "请输入兑换码。"; statusColor = Color.DarkRed; return; }
        status = "正在兑换额度…"; statusColor = Color.Gray;
        pendingRedeem = redeem(authToken, code);
    }

    private void StartBalanceRefresh()
    {
        if (authToken.Length == 0 || refreshBalance is null || pendingBalance is not null)
            return;
        try { pendingBalance = refreshBalance(authToken); }
        catch { status = "无法读取托管余额，请重新登录。"; statusColor = Color.DarkRed; }
    }

    private string FormatBalance()
    {
        if (balanceMicros < 0)
            return "托管余额：登录后显示";
        decimal amount = balanceMicros / 1_000_000m;
        return "托管余额：¥" + amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void OpenPurchasePage(int denomination)
    {
        if (!PurchaseUrls.TryGetValue(denomination, out string? url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            status = $"已打开 ¥{denomination} 购买页面，购买后请将卡密粘贴到兑换框。";
            statusColor = Color.DarkGreen;
        }
        catch
        {
            status = $"无法自动打开购买页面，请手动访问：{url}";
            statusColor = Color.DarkRed;
        }
    }

    private void Reposition()
    {
        width = Math.Min(900, Math.Max(680, Game1.uiViewport.Width - 48));
        height = Math.Min(700, Math.Max(600, Game1.uiViewport.Height - 48));
        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

        int fieldX = xPositionOnScreen + 36;
        int fieldWidth = width - 72;
        int firstFieldY = yPositionOnScreen + 118;
        emailBox.X = fieldX; emailBox.Y = firstFieldY; emailBox.Width = fieldWidth; emailBox.Height = 44;
        passwordBox.X = fieldX; passwordBox.Y = emailBox.Y + emailBox.Height + 22; passwordBox.Width = fieldWidth; passwordBox.Height = 44;
        modelBox.X = fieldX; modelBox.Y = passwordBox.Y + passwordBox.Height + 22; modelBox.Width = fieldWidth; modelBox.Height = 44;
        redeemBox.X = fieldX; redeemBox.Y = modelBox.Y + modelBox.Height + 22; redeemBox.Width = Math.Max(360, fieldWidth - 158); redeemBox.Height = 44;
        redeemButton.bounds = new Rectangle(redeemBox.X + redeemBox.Width + 12, redeemBox.Y, fieldWidth - redeemBox.Width - 12, 44);

        int purchaseY = redeemBox.Y + redeemBox.Height + 104;
        const int purchaseGap = 8;
        int purchaseWidth = (fieldWidth - purchaseGap * 3) / 4;
        int index = 0;
        foreach (ClickableComponent button in purchaseButtons.Values)
        {
            button.bounds = new Rectangle(fieldX + index * (purchaseWidth + purchaseGap), purchaseY, purchaseWidth, 44);
            index++;
        }

        int buttonY = yPositionOnScreen + height - 68;
        int actionWidth = Math.Min(180, (fieldWidth - 28) / 3);
        loginButton.bounds = new Rectangle(fieldX, buttonY, actionWidth, 48);
        registerButton.bounds = new Rectangle(loginButton.bounds.Right + 14, buttonY, actionWidth, 48);
        directSettingsButton.bounds = new Rectangle(registerButton.bounds.Right + 14, buttonY, fieldWidth - actionWidth * 2 - 28, 48);
        initializeUpperRightCloseButton();
    }

    private void Close()
    {
        if (closed) return; closed = true; ClearKeyboard(); exitThisMenuNoSound();
    }

    private void OpenDirectSettings()
    {
        if (onOpenDirectSettings is null)
        {
            status = "高级直连设置当前不可用。";
            statusColor = Color.DarkRed;
            return;
        }

        closed = true;
        ClearKeyboard();
        exitThisMenuNoSound();
        onOpenDirectSettings();
    }

    protected override void cleanupBeforeExit() { ClearKeyboard(); base.cleanupBeforeExit(); }
    private void ClearKeyboard() { if (ReferenceEquals(Game1.keyboardDispatcher.Subscriber, emailBox) || ReferenceEquals(Game1.keyboardDispatcher.Subscriber, passwordBox) || ReferenceEquals(Game1.keyboardDispatcher.Subscriber, modelBox) || ReferenceEquals(Game1.keyboardDispatcher.Subscriber, redeemBox)) Game1.keyboardDispatcher.Subscriber = null; }
    private void Select(TextBox box) { emailBox.Selected = ReferenceEquals(box, emailBox); passwordBox.Selected = ReferenceEquals(box, passwordBox); modelBox.Selected = ReferenceEquals(box, modelBox); redeemBox.Selected = ReferenceEquals(box, redeemBox); box.SelectMe(); Game1.keyboardDispatcher.Subscriber = box; }
    private static TextBox CreateTextBox(Texture2D texture, int limit, bool password) => new(texture, null, Game1.smallFont, Game1.textColor) { textLimit = limit, PasswordBox = password, TitleText = string.Empty };
    private static bool Contains(TextBox box, int x, int y) => new Rectangle(box.X, box.Y, box.Width, box.Height).Contains(x, y);
    private static void DrawButton(SpriteBatch b, ClickableComponent component, string label, bool disabled) { drawTextureBox(b, component.bounds.X, component.bounds.Y, component.bounds.Width, component.bounds.Height, disabled ? Color.Gray : Color.White); Vector2 size = Game1.smallFont.MeasureString(label); b.DrawString(Game1.smallFont, label, new Vector2(component.bounds.Center.X - size.X / 2f, component.bounds.Center.Y - size.Y / 2f), disabled ? Color.DarkGray : Game1.textColor); }
}
