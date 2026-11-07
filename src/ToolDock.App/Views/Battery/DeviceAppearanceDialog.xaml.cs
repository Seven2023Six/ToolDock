using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using VigilWatt.Core.Models;
using VigilWatt.Core.Settings;

namespace ToolDock.App.Views;

/// <summary>
/// 设备外观对话框（移植 VigilWatt 的设备外观）：重命名 + 自定义图标。
/// 保存写入 settings.DeviceCustomizations，由 DevicePresentation 在展示层套用。
/// </summary>
public partial class DeviceAppearanceDialog : Window
{
    private readonly ObservableCollection<IconChoice> _choices = new();
    private readonly string _deviceId;
    private readonly DeviceKind _automaticKind;
    private readonly string _automaticName;

    /// <summary>用户点了「恢复默认」。</summary>
    public bool ResetRequested { get; private set; }

    /// <summary>保存后的自定义项（null = 不动）。</summary>
    public DeviceCustomization? Result { get; private set; }

    public DeviceAppearanceDialog(string deviceId, string displayName, DeviceKind currentKind, DeviceKind automaticKind)
    {
        InitializeComponent();
        _deviceId = deviceId;
        _automaticName = displayName;
        _automaticKind = automaticKind;
        NameBox.Text = displayName;

        // 图标候选：自动 / 通用 / 九种设备类型
        var options = new (DeviceKind? Value, DeviceKind Preview, string Label)[]
        {
            (null, automaticKind, "自动"),
            (DeviceKind.Unknown, DeviceKind.Unknown, "通用"),
            (DeviceKind.Mouse, DeviceKind.Mouse, "鼠标"),
            (DeviceKind.Keyboard, DeviceKind.Keyboard, "键盘"),
            (DeviceKind.Headset, DeviceKind.Headset, "耳机"),
            (DeviceKind.Speaker, DeviceKind.Speaker, "音箱"),
            (DeviceKind.Gamepad, DeviceKind.Gamepad, "手柄"),
            (DeviceKind.Computer, DeviceKind.Computer, "电脑"),
            (DeviceKind.Phone, DeviceKind.Phone, "手机"),
            (DeviceKind.Tablet, DeviceKind.Tablet, "平板"),
            (DeviceKind.Watch, DeviceKind.Watch, "手表"),
        };

        DeviceKind? selected = currentKind == automaticKind ? null : currentKind;
        foreach (var option in options)
        {
            var choice = new IconChoice
            {
                Value = option.Value,
                Label = option.Label,
                Glyph = Glyph(option.Preview),
                IsSelected = option.Value == selected,
            };
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IconChoice.IsSelected) && choice.IsSelected)
                {
                    foreach (var other in _choices.Where(c => c != choice))
                    {
                        other.IsSelected = false;
                    }
                }
            };
            _choices.Add(choice);
        }
        // 没匹配到任何项时回落到「自动」
        if (!_choices.Any(c => c.IsSelected))
        {
            _choices[0].IsSelected = true;
        }

        IconList.ItemsSource = _choices;
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>当前选中的设备类型映射（与设备池一致，图标字形在两处保持同源）。</summary>
    private static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "\uE962",
        DeviceKind.Keyboard => "\uE765",
        DeviceKind.Headset => "\uE7F5",
        DeviceKind.Speaker => "\uE7F6",
        DeviceKind.Gamepad => "\uE7FC",
        DeviceKind.Computer => "\uE7F4",
        DeviceKind.Phone => "\uE8EA",
        DeviceKind.Tablet => "\uE70A",
        DeviceKind.Watch => "\uE916",
        _ => "\uE7C3",
    };

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var selected = _choices.FirstOrDefault(c => c.IsSelected)?.Value;

        // 名字与自动名称相同、图标为自动 → 视为没有自定义
        bool nameIsDefault = string.IsNullOrEmpty(name) || name == _automaticName;
        Result = new DeviceCustomization
        {
            Name = nameIsDefault ? null : name,
            IconKind = selected,
        };
        if (Result.Name is null && Result.IconKind is null)
        {
            ResetRequested = true;
        }
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        ResetRequested = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>图标候选。</summary>
public sealed class IconChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public DeviceKind? Value { get; init; }
    public string Label { get; init; } = "";
    public string Glyph { get; init; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
