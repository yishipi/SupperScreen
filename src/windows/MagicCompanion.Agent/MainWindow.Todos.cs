using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MagicCompanion.Agent;

internal sealed partial class MainWindow
{
    private const string TodoTextDefault = "#18243A";
    private const string TodoTextCompleted = "#54637A";
    private const string TodoSurface = "#FFFFFF";
    private Window? quickTodoWindow;

    private StackPanel BuildTodos()
    {
        var editor = new TodoEditor();
        var panel = new StackPanel();
        panel.Children.Add(editor.Build("添加", () => Try(() => SaveTodo(editor))));
        var tools = new WrapPanel();
        tools.Children.Add(Button("撤销上次删除", () => Try(() => store.ChangeTodo("undo"))));
        tools.Children.Add(Button("清除已完成", () => Try(() => store.ChangeTodo("clearCompleted"))));
        panel.Children.Add(tools);
        panel.Children.Add(todoRows);
        return panel;
    }

    private void SaveTodo(TodoEditor editor, string? id = null)
    {
        store.ChangeTodo(id == null ? "add" : "edit", id, editor.Title.Text,
            textColor: editor.Foreground.Value, backgroundColor: editor.Background.Value);
        if (id == null)
        {
            editor.Title.Clear();
            editor.Title.Focus();
        }
    }

    private void RenderTodos() => RenderTodoItems(todoRows, store.Todos(), includeActions: true, Try);

    private void RenderTodoItems(Panel rows, TodoSnapshot snapshot, bool includeActions, Action<Action> apply)
    {
        rows.Children.Clear();
        var items = snapshot.Items;
        if (items.Count == 0)
            rows.Children.Add(Text("暂无待办"));
        foreach (var item in items)
        {
            var row = new DockPanel();
            if (includeActions)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                DockPanel.SetDock(actions, Dock.Right);
                row.Children.Add(actions);
                actions.Children.Add(Button("↑", () => apply(() => store.ChangeTodo("move", item.Id, direction: -1))));
                actions.Children.Add(Button("↓", () => apply(() => store.ChangeTodo("move", item.Id, direction: 1))));
                actions.Children.Add(Button("编辑", () => ShowTodoEditor(item)));
                actions.Children.Add(Button("删除", () => apply(() => store.ChangeTodo("delete", item.Id))));
            }
            var check = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = item.Title,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush(item.TextColor ?? (item.Completed ? TodoTextCompleted : TodoTextDefault)),
                    TextDecorations = item.Completed ? TextDecorations.Strikethrough : null,
                },
                IsChecked = item.Completed,
                Margin = new Thickness(0, 0, 8, 0),
            };
            check.Click += (_, _) => apply(() => store.ChangeTodo("toggle", item.Id));
            row.Children.Add(check);
            var card = Card(row);
            card.Background = Brush(item.BackgroundColor ?? TodoDefaultBackground(item));
            rows.Children.Add(card);
        }
    }

    private static string TodoDefaultBackground(TodoItem? item)
    {
        if (item?.Color is not { } accent) return TodoSurface;
        var color = (Color)ColorConverter.ConvertFromString(accent);
        var percent = item.Completed ? 4 : 8;
        int Blend(byte channel) => (channel * percent + 255 * (100 - percent) + 50) / 100;
        return $"#{Blend(color.R):X2}{Blend(color.G):X2}{Blend(color.B):X2}";
    }

    private void QuickTodo()
    {
        if (quickTodoWindow is { } previous)
        {
            previous.Close();
            return;
        }

        var window = new Window
        {
            Title = "SupperScreen 待办",
            Width = 460,
            Height = 400,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var body = new DockPanel { Margin = new Thickness(14) };
        var surface = new Border
        {
            Child = body,
            Background = Brush("#F2F4F8"),
            BorderBrush = Brush("#DCE3EE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
        };
        window.Content = surface;
        void AddTop(UIElement child)
        {
            DockPanel.SetDock(child, Dock.Top);
            body.Children.Add(child);
        }
        var header = new DockPanel { Background = Brushes.Transparent };
        var close = Button("关闭", window.Close);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var title = Text("待办", 17);
        title.FontWeight = FontWeights.SemiBold;
        header.Children.Add(title);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                window.DragMove();
        };
        AddTop(header);
        var editor = new TodoEditor();
        var error = Text("");
        error.Foreground = Brush("#A54020");
        error.Visibility = Visibility.Collapsed;
        void Apply(Action action)
        {
            try
            {
                action();
                error.Visibility = Visibility.Collapsed;
            }
            catch (AgentException ex)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
            }
        }
        AddTop(editor.Build("添加", () => Apply(() => SaveTodo(editor))));
        AddTop(error);
        var rows = new StackPanel();
        body.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        void Update(TodoSnapshot snapshot)
        {
            title.Text = $"待办 · {snapshot.Items.Count(item => !item.Completed)} 项未完成";
            RenderTodoItems(rows, snapshot, includeActions: false, Apply);
        }
        void OnChanged(TodoSnapshot snapshot) => Dispatcher.BeginInvoke(() =>
        {
            if (quickTodoWindow == window) Update(snapshot);
        });
        Update(store.Todos());
        store.TodosChanged += OnChanged;
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            store.TodosChanged -= OnChanged;
            quickTodoWindow = null;
        };
        quickTodoWindow = window;
        window.Show();
        window.Activate();
        editor.Title.Focus();
    }

    private void ShowTodoEditor(TodoItem item)
    {
        var dialog = Dialog("编辑待办", 580, 220);
        dialog.ResizeMode = ResizeMode.NoResize;
        var editor = new TodoEditor(item);
        var panel = new StackPanel { Margin = new Thickness(18) };
        var error = Text("");
        error.Foreground = Brush("#A54020");
        panel.Children.Add(editor.Build("保存", () =>
        {
            try
            {
                SaveTodo(editor, item.Id);
                dialog.Close();
            }
            catch (AgentException ex) { error.Text = ex.Message; }
        }));
        panel.Children.Add(error);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => editor.Title.Focus();
        dialog.ShowDialog();
    }

    private sealed class TodoEditor(TodoItem? item = null)
    {
        public TextBox Title { get; } = new() { Text = item?.Title ?? "", MaxLength = 160, ToolTip = "输入待办，回车保存" };
        public TodoColorField Foreground { get; } = new("文字色", item?.TextColor, item?.Completed == true ? TodoTextCompleted : TodoTextDefault);
        public TodoColorField Background { get; } = new("背景色", item?.BackgroundColor, TodoDefaultBackground(item));

        public StackPanel Build(string action, Action save)
        {
            var editor = new StackPanel();
            var row = new DockPanel();
            var submit = Button(action, save, true);
            DockPanel.SetDock(submit, Dock.Right);
            row.Children.Add(submit);
            row.Children.Add(Title);
            Title.VerticalAlignment = VerticalAlignment.Center;
            Title.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                save();
            };
            editor.Children.Add(row);
            var colors = new WrapPanel();
            colors.Children.Add(Foreground.Control);
            colors.Children.Add(Background.Control);
            editor.Children.Add(colors);
            return editor;
        }
    }

    private sealed class TodoColorField
    {
        private static readonly string[] Palette =
        [
            "#18243A", "#245BC2", "#168463", "#A95B04", "#CF3C48", "#8555B8",
            "#FFFFFF", "#E6EFFF", "#DDF7E9", "#FFF0DA", "#FCE8EB", "#F0E7FD",
        ];
        private readonly string label;
        private readonly string defaultHex;
        public string? Value { get; private set; }
        public Button Control { get; }

        public TodoColorField(string label, string? value, string defaultHex)
        {
            this.label = label;
            this.defaultHex = defaultHex;
            Value = value;
            Control = CreateButton(Chip($"{label} · {Value ?? "默认"}", Value ?? defaultHex));
            Control.Click += (_, _) => Pick();
        }

        private void Pick()
        {
            var dialog = new Window
            {
                Owner = Window.GetWindow(Control),
                Title = label,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
            };
            var panel = new StackPanel { Margin = new Thickness(16) };
            var hex = new TextBox { Text = Value ?? defaultHex, MaxLength = 7, ToolTip = "#RRGGBB" };
            var preview = new ContentControl { Content = Chip("预览", hex.Text), Margin = new Thickness(0, 8, 0, 4) };
            var error = Text("");
            error.Foreground = Brush("#A54020");
            hex.TextChanged += (_, _) =>
            {
                error.Text = "";
                try { preview.Content = Chip("预览", AgentStore.ValidateColor(hex.Text)!); }
                catch (AgentException) { preview.Content = null; }
            };
            var palette = new UniformGrid { Columns = 6 };
            foreach (var color in Palette)
            {
                var choice = CreateButton(Chip(null, color));
                choice.ToolTip = color;
                choice.Click += (_, _) => hex.Text = color;
                System.Windows.Automation.AutomationProperties.SetName(choice, color);
                palette.Children.Add(choice);
            }
            panel.Children.Add(palette);
            panel.Children.Add(Text("自定义颜色（#RRGGBB）"));
            panel.Children.Add(hex);
            panel.Children.Add(preview);
            panel.Children.Add(error);
            void Apply(string? color)
            {
                Value = color;
                Control.Content = Chip($"{label} · {Value ?? "默认"}", Value ?? defaultHex);
                dialog.Close();
            }
            var actions = new WrapPanel();
            var confirm = Button("应用", () =>
            {
                try { Apply(AgentStore.ValidateColor(hex.Text)); }
                catch (AgentException ex) { error.Text = ex.Message; }
            }, true);
            confirm.IsDefault = true;
            actions.Children.Add(confirm);
            actions.Children.Add(Button("恢复默认", () => Apply(null)));
            var cancel = Button("取消", dialog.Close);
            cancel.IsCancel = true;
            actions.Children.Add(cancel);
            panel.Children.Add(actions);
            dialog.Content = panel;
            dialog.ShowDialog();
        }

        private static StackPanel Chip(string? text, string hex)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                Background = Brush(hex),
                BorderBrush = Brush("#CBD5E1"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (text != null)
                row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0) });
            return row;
        }
    }
}
