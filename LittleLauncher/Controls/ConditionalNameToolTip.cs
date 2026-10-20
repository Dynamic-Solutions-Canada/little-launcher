// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace LittleLauncher.Controls;

/// <summary>
/// Gives an item its full name as a tooltip only when its label is absent or trimmed.
/// Attach Text to the template root and optionally name its label with LabelName.
/// Attach directly to a TextBlock to use its actual trimming and target only that label.
/// </summary>
public sealed class ConditionalNameToolTip : DependencyObject
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(ConditionalNameToolTip),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty LabelNameProperty = DependencyProperty.RegisterAttached(
        "LabelName", typeof(string), typeof(ConditionalNameToolTip),
        new PropertyMetadata(string.Empty, OnLabelNameChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(object), typeof(ConditionalNameToolTip), new PropertyMetadata(null));

    /// <summary>Gets the complete item name, independently of its visible label.</summary>
    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    /// <summary>Binds the complete item name so renaming and recycled data contexts update it.</summary>
    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    /// <summary>Gets the label's name within the template; empty means there is no label.</summary>
    public static string GetLabelName(DependencyObject element) => (string)element.GetValue(LabelNameProperty);

    /// <summary>Identifies the TextBlock whose actual trimming controls tooltip availability.</summary>
    public static void SetLabelName(DependencyObject element, string value) => element.SetValue(LabelNameProperty, value);

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element)
        {
            GetState(element).Update();
        }
    }

    private static void OnLabelNameChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element)
        {
            GetState(element).Connect();
        }
    }

    /// <summary>Keeps subscriptions with the template, rather than retaining containers globally.</summary>
    private static State GetState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is not State state)
        {
            state = new State(element);
            element.SetValue(StateProperty, state);
            state.Connect();
        }

        return state;
    }

    private sealed class State
    {
        private readonly FrameworkElement _root;
        private FrameworkElement? _target;
        private TextBlock? _label;
        private ToolTip? _toolTip;

        internal State(FrameworkElement root)
        {
            _root = root;
            root.Loaded += OnLoaded;
            root.Unloaded += OnUnloaded;
        }

        /// <summary>Resolves the current container and label after template realization.</summary>
        internal void Connect()
        {
            Disconnect();
            if (!_root.IsLoaded)
            {
                return;
            }

            _target = _root;
            if (_root is TextBlock selfLabel)
            {
                // A group/column caption owns only its own tooltip. Targeting an enclosing
                // group item would expose its name while the pointer is over child items.
                _label = selfLabel;
            }
            else
            {
                // Item templates share their tooltip across icon, label and padding.
                // SelectorItem covers both ListViewItem and GridViewItem, including children.
                for (DependencyObject? ancestor = VisualTreeHelper.GetParent(_root);
                     ancestor != null;
                     ancestor = VisualTreeHelper.GetParent(ancestor))
                {
                    if (ancestor is SelectorItem container)
                    {
                        _target = container;
                        break;
                    }
                }

                string labelName = GetLabelName(_root);
                if (!string.IsNullOrEmpty(labelName))
                {
                    _label = _root.FindName(labelName) as TextBlock;
                }
            }

            if (_label != null)
            {
                _label.IsTextTrimmedChanged += OnTrimmedChanged;
            }

            Update();
        }

        /// <summary>Refreshes content and availability without measuring or changing item layout.</summary>
        internal void Update()
        {
            if (_target == null)
            {
                return;
            }

            string name = GetText(_root);
            bool hasNoLabel = _root is not TextBlock && string.IsNullOrEmpty(GetLabelName(_root));
            bool needsToolTip = !string.IsNullOrWhiteSpace(name) && (hasNoLabel || _label?.IsTextTrimmed == true);
            if (!needsToolTip)
            {
                ClearToolTip();
                return;
            }

            // Native tooltips already escape the small flyout HWND without changing its size.
            _toolTip ??= new ToolTip();
            _toolTip.Content = name;
            if (!ReferenceEquals(ToolTipService.GetToolTip(_target), _toolTip))
            {
                ToolTipService.SetToolTip(_target, _toolTip);
            }
        }

        /// <summary>Releases label events and the container when this template is unloaded or reused.</summary>
        private void Disconnect()
        {
            if (_label != null)
            {
                _label.IsTextTrimmedChanged -= OnTrimmedChanged;
                _label = null;
            }

            ClearToolTip();
            _target = null;
        }

        /// <summary>Closes a pending/open tooltip, preserving any newer template's tooltip.</summary>
        private void ClearToolTip()
        {
            if (_toolTip != null)
            {
                _toolTip.IsOpen = false;
                if (_target != null && ReferenceEquals(ToolTipService.GetToolTip(_target), _toolTip))
                {
                    ToolTipService.SetToolTip(_target, null);
                }
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Connect();

        private void OnUnloaded(object sender, RoutedEventArgs args) => Disconnect();

        private void OnTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => Update();
    }
}
