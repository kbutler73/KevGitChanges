using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace KevGitChanges
{
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(MarginName)]
    [MarginContainer(PredefinedMarginNames.LeftSelection)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CommittedChangeTextMarginProvider : IWpfTextViewMarginProvider
    {
        internal const string MarginName = "KevGitChangesCommittedTextMargin";

        [Import]
        internal ITextDocumentFactoryService TextDocumentFactoryService = null;

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            var view = wpfTextViewHost?.TextView;
            if (view == null)
            {
                return null;
            }

            var tracker = view.Properties.GetOrCreateSingletonProperty(
                () => new CommittedChangeTracker(view, view.TextBuffer, TextDocumentFactoryService));

            return new CommittedChangeTextMargin(view, tracker);
        }
    }

    internal sealed class CommittedChangeTextMargin : Canvas, IWpfTextViewMargin
    {
        private static readonly Brush AddedBrush = CreateBrush(34, 197, 94);
        private static readonly Brush ModifiedBrush = CreateBrush(59, 130, 246);
        private static readonly Brush DeletedBrush = CreateBrush(239, 68, 68);
        private readonly IWpfTextView view;
        private readonly CommittedChangeTracker tracker;
        private bool isDisposed;

        public CommittedChangeTextMargin(IWpfTextView view, CommittedChangeTracker tracker)
        {
            this.view = view;
            this.tracker = tracker;

            Width = 4;
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            Background = Brushes.Transparent;
            IsHitTestVisible = false;

            tracker.ChangesUpdated += Tracker_ChangesUpdated;
            view.LayoutChanged += View_LayoutChanged;
            SizeChanged += Margin_SizeChanged;
            ToolWindow1Package.OptionsChanged += ToolWindow1Package_OptionsChanged;
            ApplyOptions();
        }

        public bool Enabled => !isDisposed;

        public double MarginSize => ActualWidth;

        public FrameworkElement VisualElement => this;

        public ITextViewMargin GetTextViewMargin(string marginName)
        {
            return string.Equals(marginName, CommittedChangeTextMarginProvider.MarginName, StringComparison.Ordinal)
                ? this
                : null;
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;
            tracker.ChangesUpdated -= Tracker_ChangesUpdated;
            view.LayoutChanged -= View_LayoutChanged;
            SizeChanged -= Margin_SizeChanged;
            ToolWindow1Package.OptionsChanged -= ToolWindow1Package_OptionsChanged;
            Children.Clear();
        }

        private void ToolWindow1Package_OptionsChanged(object sender, EventArgs e)
        {
            ApplyOptions();
            Redraw();
        }

        private void Tracker_ChangesUpdated(object sender, EventArgs e)
        {
            Redraw();
        }

        private void View_LayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            Redraw();
        }

        private void Margin_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Redraw();
        }

        private void Redraw()
        {
            Children.Clear();

            if (isDisposed)
            {
                return;
            }

            if (!IsLineMarkersEnabled())
            {
                return;
            }

            var snapshot = tracker.CurrentSnapshot;
            var changes = tracker.CurrentChanges;
            var visibleLines = view.TextViewLines;
            if (snapshot == null || changes == null || changes.Count == 0 || visibleLines == null || ActualWidth <= 0)
            {
                return;
            }

            var visibleChangeMap = BuildVisibleChangeMap(changes);
            foreach (var line in visibleLines)
            {
                var lineNumber = line.Start.GetContainingLine().LineNumber + 1;
                if (!visibleChangeMap.TryGetValue(lineNumber, out var kind))
                {
                    continue;
                }

                var top = line.TextTop - view.ViewportTop;
                var height = Math.Max(2.0, line.TextHeight);
                if (top + height < 0 || top > ActualHeight)
                {
                    continue;
                }

                var rect = new Rectangle
                {
                    Width = Math.Max(2.0, Math.Min(4.0, ActualWidth)),
                    Height = Math.Max(2.0, height - 1),
                    Fill = GetBrush(kind),
                    RadiusX = 0.5,
                    RadiusY = 0.5,
                    IsHitTestVisible = false
                };

                SetLeft(rect, Math.Max(0.0, (ActualWidth - rect.Width) / 2.0));
                SetTop(rect, Math.Max(0.0, top));
                Children.Add(rect);
            }
        }

        private static Dictionary<int, CommittedChangeKind> BuildVisibleChangeMap(IReadOnlyList<CommittedLineChange> changes)
        {
            var map = new Dictionary<int, CommittedChangeKind>();
            for (var i = 0; i < changes.Count; i++)
            {
                map[changes[i].LineNumber] = changes[i].Kind;
            }

            return map;
        }

        private static Brush GetBrush(CommittedChangeKind kind)
        {
            switch (kind)
            {
                case CommittedChangeKind.Added:
                    return AddedBrush;
                case CommittedChangeKind.Deleted:
                    return DeletedBrush;
                default:
                    return ModifiedBrush;
            }
        }

        private static Brush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private void ApplyOptions()
        {
            Visibility = IsLineMarkersEnabled() ? Visibility.Visible : Visibility.Collapsed;
        }

        private static bool IsLineMarkersEnabled()
        {
            try
            {
                return ToolWindow1Package.GetOptions()?.ShowLineMarkers != false;
            }
            catch
            {
                return true;
            }
        }
    }
}
