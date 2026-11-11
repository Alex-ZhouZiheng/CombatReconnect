using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.addons.mega_text;

namespace CombatReconnect;

internal static class RewindUiController
{
    private static PanelContainer? _historyPanel;
    private static VBoxContainer? _historyList;
    private static Button? _rewindButton;
    private static Control? _host;
    private static ColorRect? _transactionOverlay;
    private static Label? _transactionLabel;
    private static HBoxContainer? _transactionButtons;
    private static int _transactionStatusGeneration;
    private static bool _attachScheduled;
    private static bool _dragging;
    private static bool _dragMoved;
    private static bool _suppressNextPress;
    private static Vector2 _dragStartMouse;
    private static Vector2 _dragStartPosition;

    internal static void ScheduleAttach()
    {
        if (_rewindButton != null || _attachScheduled) return;
        _attachScheduled = true;
        _ = AttachWhenCombatUiReady();
    }

    private static async Task AttachWhenCombatUiReady()
    {
        try
        {
            SceneTree tree = (SceneTree)Engine.GetMainLoop();
            for (int frame = 0; frame < 60; frame++)
            {
                if (NCombatRoom.Instance?.Ui is Control host)
                {
                    Attach(host);
                    return;
                }
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            ReconnectLog.Write("Rewind button was not attached: combat UI did not become available.");
        }
        catch (Exception ex)
        {
            ReconnectLog.Write($"Could not attach rewind button: {ex}");
        }
        finally
        {
            _attachScheduled = false;
        }
    }

    private static void Attach(Control host)
    {
        if (_rewindButton != null) return;
        _rewindButton = new Button
        {
            Name = "CombatReconnectRewindButton",
            Text = "↶ 回滚",
            TooltipText = "左键：回到当前回合；右键：选择历史回合",
            Position = new Vector2(96, 756),
            Size = new Vector2(150, 42),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _host = host;
        ApplyLocaleFont(_rewindButton);
        _rewindButton.Pressed += OnRewindButtonPressed;
        _rewindButton.GuiInput += OnRewindButtonInput;
        host.AddChild(_rewindButton);
        ApplySavedPosition();

        _historyPanel = new PanelContainer
        {
            Name = "CombatReconnectTurnHistory",
            Position = new Vector2(24, 166),
            Size = new Vector2(240, 300),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _historyList = new VBoxContainer { Name = "Entries" };
        _historyPanel.AddChild(_historyList);
        host.AddChild(_historyPanel);
        MoveButton(_rewindButton.Position);
        RefreshHistory();
    }

    private static void OnRewindButtonPressed()
    {
        if (_suppressNextPress)
        {
            _suppressNextPress = false;
            return;
        }
        RewindCoordinator.RequestCurrentTurn();
    }

    private static void OnRewindButtonInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
        {
            ToggleHistory();
            return;
        }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } pressed)
        {
            _dragging = true;
            _dragMoved = false;
            _dragStartMouse = pressed.GlobalPosition;
            _dragStartPosition = _rewindButton?.Position ?? Vector2.Zero;
            return;
        }
        if (input is InputEventMouseMotion motion && _dragging)
        {
            MoveButton(_dragStartPosition + motion.GlobalPosition - _dragStartMouse);
            _dragMoved = true;
            return;
        }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } released && _dragging)
        {
            _dragging = false;
            _suppressNextPress = _dragMoved;
            SavePosition();
        }
    }

    private static void ApplySavedPosition()
    {
        if (_host == null || _rewindButton == null) return;
        if (_host.Size.X <= 0 || _host.Size.Y <= 0)
        {
            MoveButton(new Vector2(96, 756));
            return;
        }
        if (RewindSettingsStore.RequiresButtonLayoutMigration)
        {
            MoveButton(GetPositionAboveEnergy());
            SavePosition();
            return;
        }
        Vector2 position = RewindSettingsStore.RewindButtonPosition;
        MoveButton(new Vector2(position.X * _host.Size.X, position.Y * _host.Size.Y));
    }

    private static Vector2 GetPositionAboveEnergy()
    {
        if (_host == null || _rewindButton == null) return new Vector2(96, 756);
        Control? energy = NCombatRoom.Instance?.Ui?.EnergyCounterContainer;
        if (energy == null || !energy.IsInsideTree()) return new Vector2(96, 756);
        Rect2 energyBounds = energy.GetGlobalRect();
        Rect2 hostBounds = _host.GetGlobalRect();
        Vector2 energyTopLeft = energyBounds.Position - hostBounds.Position;
        return new Vector2(
            energyTopLeft.X + (energyBounds.Size.X - _rewindButton.Size.X) / 2f,
            energyTopLeft.Y - _rewindButton.Size.Y - 12f);
    }

    private static void MoveButton(Vector2 position)
    {
        if (_host == null || _rewindButton == null) return;
        Vector2 max = new(
            Math.Max(0f, _host.Size.X - _rewindButton.Size.X),
            Math.Max(0f, _host.Size.Y - _rewindButton.Size.Y));
        _rewindButton.Position = position.Clamp(Vector2.Zero, max);
        if (_historyPanel != null)
            _historyPanel.Position = _rewindButton.Position + new Vector2(0, _rewindButton.Size.Y + 6);
    }

    private static void SavePosition()
    {
        if (_host == null || _rewindButton == null || _host.Size.X <= 0 || _host.Size.Y <= 0) return;
        RewindSettingsStore.SaveRewindButtonPosition(new Vector2(
            _rewindButton.Position.X / _host.Size.X,
            _rewindButton.Position.Y / _host.Size.Y));
    }

    private static void ToggleHistory()
    {
        if (_historyPanel == null) return;
        _historyPanel.Visible = !_historyPanel.Visible;
        if (_historyPanel.Visible) RefreshHistory();
    }

    internal static void RefreshHistory()
    {
        if (_historyList == null) return;
        foreach (Node child in _historyList.GetChildren()) child.QueueFree();

        var title = new Label { Text = "回合历史" };
        ApplyLocaleFont(title);
        _historyList.AddChild(title);
        IReadOnlyList<TurnHistoryMetadata> entries = MultiplayerRewindCoordinator.GetHistory();
        if (entries.Count == 0)
        {
            var empty = new Label { Text = "暂无可用锚点" };
            ApplyLocaleFont(empty);
            _historyList.AddChild(empty);
            return;
        }

        foreach (TurnHistoryMetadata entry in entries.OrderByDescending(item => item.epoch))
        {
            TurnHistoryMetadata selected = entry;
            var button = new Button
            {
                Text = $"第 {selected.roundNumber} 回合  ·  #{selected.epoch}",
                TooltipText = selected.displayMetadata ?? string.Empty,
                CustomMinimumSize = new Vector2(220, 34)
            };
            ApplyLocaleFont(button);
            button.Pressed += () =>
            {
                if (_historyPanel != null) _historyPanel.Visible = false;
                RewindCoordinator.RequestTurn(selected.epoch);
            };
            _historyList.AddChild(button);
        }
    }

    internal static void ShowVoteRequest(int roundNumber, int timeoutSeconds, Action<bool> respond)
    {
        _transactionStatusGeneration++;
        EnsureTransactionOverlay();
        if (_transactionLabel == null || _transactionButtons == null) return;
        _transactionLabel.Text = $"有玩家请求回滚到第 {roundNumber} 回合\n是否同意？（{timeoutSeconds} 秒后取消）";
        ClearTransactionButtons();
        var approve = new Button { Text = "同意", CustomMinimumSize = new Vector2(120, 42) };
        var reject = new Button { Text = "拒绝", CustomMinimumSize = new Vector2(120, 42) };
        ApplyLocaleFont(approve);
        ApplyLocaleFont(reject);
        approve.Pressed += () => { ShowTransactionStatus("已同意，等待其他玩家投票……"); respond(true); };
        reject.Pressed += () => { ShowTransactionStatus("已拒绝，正在取消回滚……"); respond(false); };
        _transactionButtons.AddChild(approve);
        _transactionButtons.AddChild(reject);
        if (_transactionOverlay != null) _transactionOverlay.Visible = true;
    }

    internal static void ShowTransactionStatus(string message)
    {
        _transactionStatusGeneration++;
        EnsureTransactionOverlay();
        if (_transactionLabel != null) _transactionLabel.Text = message;
        ClearTransactionButtons();
        if (_transactionOverlay != null) _transactionOverlay.Visible = true;
    }

    internal static void HideTransactionStatus()
    {
        _transactionStatusGeneration++;
        if (_transactionOverlay != null) _transactionOverlay.Visible = false;
        ClearTransactionButtons();
    }

    internal static void ShowTransientStatus(string message, int milliseconds = 6000)
    {
        ShowTransactionStatus(message);
        int generation = _transactionStatusGeneration;
        _ = HideTransientStatus(generation, milliseconds);
    }

    private static async Task HideTransientStatus(int generation, int milliseconds)
    {
        await Task.Delay(milliseconds);
        if (_transactionStatusGeneration == generation) HideTransactionStatus();
    }

    internal static void SetRewindControlsDisabled(bool disabled)
    {
        if (_rewindButton != null) _rewindButton.Disabled = disabled;
        if (disabled && _historyPanel != null) _historyPanel.Visible = false;
    }

    private static void EnsureTransactionOverlay()
    {
        if (_transactionOverlay != null || _host == null) return;
        _transactionOverlay = new ColorRect
        {
            Name = "CombatReconnectTransactionOverlay",
            Color = new Color(0, 0, 0, 0.72f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 1000
        };
        _transactionOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(520, 190) };
        var content = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        content.AddThemeConstantOverride("separation", 18);
        _transactionLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        ApplyLocaleFont(_transactionLabel);
        _transactionButtons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        content.AddChild(_transactionLabel);
        content.AddChild(_transactionButtons);
        panel.AddChild(content);
        center.AddChild(panel);
        _transactionOverlay.AddChild(center);
        _host.AddChild(_transactionOverlay);
        _transactionOverlay.Visible = false;
    }

    private static void ApplyLocaleFont(Control control)
        => control.ApplyLocaleFontSubstitution(FontType.Regular, ThemeConstants.Label.Font);

    private static void ClearTransactionButtons()
    {
        if (_transactionButtons == null) return;
        foreach (Node child in _transactionButtons.GetChildren()) child.QueueFree();
    }

    internal static void Remove()
    {
        _rewindButton?.QueueFree();
        _historyPanel?.QueueFree();
        _rewindButton = null;
        _historyPanel = null;
        _historyList = null;
        _host = null;
        _transactionOverlay?.QueueFree();
        _transactionOverlay = null;
        _transactionLabel = null;
        _transactionButtons = null;
        _attachScheduled = false;
        _dragging = false;
        _dragMoved = false;
        _suppressNextPress = false;
    }
}
