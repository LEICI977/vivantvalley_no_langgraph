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
    private readonly Func<string, string, bool, Task<(bool Success, string Message, string Token, string Model)>> authenticate;
    private readonly Action<string, string> onSuccess;
    private readonly Func<string, string, Task<string>>? redeem;
    private readonly Action? onOpenDirectSettings;
    private readonly TextBox emailBox;
    private readonly TextBox passwordBox;
    private readonly TextBox modelBox;
    private readonly TextBox redeemBox;
    private readonly ClickableComponent loginButton = new(Rectangle.Empty, "login");
    private readonly ClickableComponent registerButton = new(Rectangle.Empty, "register");
    private readonly ClickableComponent redeemButton = new(Rectangle.Empty, "redeem");
    private readonly ClickableComponent directSettingsButton = new(Rectangle.Empty, "direct-settings");
    private Task<(bool Success, string Message, string Token, string Model)>? pending;
    private Task<string>? pendingRedeem;
    private string authToken = string.Empty;
    private bool closed;
    private string status = "登录后即可使用托管模型并按账户额度结算。";
    private Color statusColor = Game1.textColor;

    public HostedAccountMenu(
        Func<string, string, bool, Task<(bool Success, string Message, string Token, string Model)>> authenticate,
        Action<string, string> onSuccess,
        Func<string, string, Task<string>>? redeem = null,
        Action? onCancel = null,
        Action? onOpenDirectSettings = null)
    {
        this.authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate));
        this.onSuccess = onSuccess ?? throw new ArgumentNullException(nameof(onSuccess));
        this.redeem = redeem;
        this.onOpenDirectSettings = onOpenDirectSettings;
        Texture2D texture = Game1.content.Load<Texture2D>("LooseSprites\\textBox");
        emailBox = CreateTextBox(texture, 320, false);
        passwordBox = CreateTextBox(texture, 320, true);
        modelBox = CreateTextBox(texture, 80, false);
        modelBox.Text = "vv-dialogue";
        redeemBox = CreateTextBox(texture, 128, false);
        width = 620;
        height = 510;
        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
        emailBox.X = xPositionOnScreen + 36; emailBox.Y = yPositionOnScreen + 128; emailBox.Width = 548; emailBox.Height = 44;
        passwordBox.X = emailBox.X; passwordBox.Y = emailBox.Y + 78; passwordBox.Width = 548; passwordBox.Height = 44;
        modelBox.X = emailBox.X; modelBox.Y = passwordBox.Y + 78; modelBox.Width = 548; modelBox.Height = 44;
        redeemBox.X = emailBox.X; redeemBox.Y = modelBox.Y + 78; redeemBox.Width = 390; redeemBox.Height = 44;
        redeemButton.bounds = new Rectangle(redeemBox.Right + 12, redeemBox.Y, 146, 44);
        loginButton.bounds = new Rectangle(emailBox.X, yPositionOnScreen + 404, 145, 48);
        registerButton.bounds = new Rectangle(loginButton.bounds.Right + 14, loginButton.bounds.Y, 145, 48);
        directSettingsButton.bounds = new Rectangle(registerButton.bounds.Right + 14, loginButton.bounds.Y, 230, 48);
        initializeUpperRightCloseButton();
        Select(emailBox);
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
                    onSuccess(result.Token, string.IsNullOrWhiteSpace(modelBox.Text) ? result.Model : modelBox.Text.Trim());
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
        }
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
        emailBox.X = xPositionOnScreen + 36; emailBox.Y = yPositionOnScreen + 128;
        passwordBox.X = emailBox.X; passwordBox.Y = emailBox.Y + 78;
        modelBox.X = emailBox.X; modelBox.Y = passwordBox.Y + 78;
        redeemBox.X = emailBox.X; redeemBox.Y = modelBox.Y + 78;
        redeemButton.bounds = new Rectangle(redeemBox.Right + 12, redeemBox.Y, 146, 44);
        loginButton.bounds = new Rectangle(emailBox.X, yPositionOnScreen + 404, 145, 48);
        registerButton.bounds = new Rectangle(loginButton.bounds.Right + 14, loginButton.bounds.Y, 145, 48);
        directSettingsButton.bounds = new Rectangle(registerButton.bounds.Right + 14, loginButton.bounds.Y, 230, 48);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (upperRightCloseButton?.containsPoint(x, y) == true) { Close(); return; }
        if (loginButton.containsPoint(x, y)) { Start(false); return; }
        if (registerButton.containsPoint(x, y)) { Start(true); return; }
        if (directSettingsButton.containsPoint(x, y)) { OpenDirectSettings(); return; }
        if (redeemButton.containsPoint(x, y) && authToken.Length > 0) { StartRedeem(); return; }
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
        upperRightCloseButton?.tryHover(x, y, 0.2f);
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.45f);
        drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
        b.DrawString(Game1.dialogueFont, "Vivant Valley 托管账户", new Vector2(xPositionOnScreen + 36, yPositionOnScreen + 24), Game1.textColor);
        b.DrawString(Game1.smallFont, "邮箱", new Vector2(emailBox.X, emailBox.Y - 26), Game1.textColor);
        b.DrawString(Game1.smallFont, "密码（至少 12 位）", new Vector2(passwordBox.X, passwordBox.Y - 26), Game1.textColor);
        b.DrawString(Game1.smallFont, "模型别名（默认 vv-dialogue，可填 vv-fast）", new Vector2(modelBox.X, modelBox.Y - 26), Game1.textColor);
        emailBox.Draw(b, false); passwordBox.Draw(b, false); modelBox.Draw(b, false);
        b.DrawString(Game1.smallFont, "兑换码（登录后可选）", new Vector2(redeemBox.X, redeemBox.Y - 26), Game1.textColor);
        redeemBox.Draw(b, false);
        DrawButton(b, redeemButton, "兑换额度", pendingRedeem is not null || authToken.Length == 0);
        DrawButton(b, loginButton, "登录", pending is not null);
        DrawButton(b, registerButton, "注册并登录", pending is not null);
        DrawButton(b, directSettingsButton, "直连 API 设置", false);
        b.DrawString(Game1.smallFont, status, new Vector2(xPositionOnScreen + 36, yPositionOnScreen + 468), statusColor);
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

    private void Close()
    {
        if (closed) return; closed = true; ClearKeyboard(); exitThisMenuNoSound();
    }

    private void OpenDirectSettings()
    {
        if (pending is not null || pendingRedeem is not null || onOpenDirectSettings is null)
            return;
        Close();
        onOpenDirectSettings();
    }

    protected override void cleanupBeforeExit() { ClearKeyboard(); base.cleanupBeforeExit(); }
    private void ClearKeyboard() { if (ReferenceEquals(Game1.keyboardDispatcher.Subscriber, emailBox) || ReferenceEquals(Game1.keyboardDispatcher.Subscriber, passwordBox) || ReferenceEquals(Game1.keyboardDispatcher.Subscriber, modelBox) || ReferenceEquals(Game1.keyboardDispatcher.Subscriber, redeemBox)) Game1.keyboardDispatcher.Subscriber = null; }
    private void Select(TextBox box) { emailBox.Selected = ReferenceEquals(box, emailBox); passwordBox.Selected = ReferenceEquals(box, passwordBox); modelBox.Selected = ReferenceEquals(box, modelBox); redeemBox.Selected = ReferenceEquals(box, redeemBox); box.SelectMe(); Game1.keyboardDispatcher.Subscriber = box; }
    private static TextBox CreateTextBox(Texture2D texture, int limit, bool password) => new(texture, null, Game1.smallFont, Game1.textColor) { textLimit = limit, PasswordBox = password, TitleText = string.Empty };
    private static bool Contains(TextBox box, int x, int y) => new Rectangle(box.X, box.Y, box.Width, box.Height).Contains(x, y);
    private static void DrawButton(SpriteBatch b, ClickableComponent component, string label, bool disabled) { drawTextureBox(b, component.bounds.X, component.bounds.Y, component.bounds.Width, component.bounds.Height, disabled ? Color.Gray : Color.White); Vector2 size = Game1.smallFont.MeasureString(label); b.DrawString(Game1.smallFont, label, new Vector2(component.bounds.Center.X - size.X / 2f, component.bounds.Center.Y - size.Y / 2f), disabled ? Color.DarkGray : Game1.textColor); }
}
