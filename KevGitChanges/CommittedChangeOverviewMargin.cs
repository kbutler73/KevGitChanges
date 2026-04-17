using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace KevGitChanges
{
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(MarginName)]
    [MarginContainer(PredefinedMarginNames.VerticalScrollBarContainer)]
    [Order(Before = PredefinedMarginNames.VerticalScrollBar)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CommittedChangeOverviewMarginProvider : IWpfTextViewMarginProvider
    {
        internal const string MarginName = "KevGitChangesCommittedOverviewMargin";

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

            return new CommittedChangeOverviewMargin(view, tracker, marginContainer);
        }
    }

    internal sealed class CommittedChangeOverviewMargin : Border, IWpfTextViewMargin
    {
        private static readonly Brush AddedBrush = CreateBrush(34, 197, 94);
        private static readonly Brush ModifiedBrush = CreateBrush(59, 130, 246);
        private static readonly Brush DeletedBrush = CreateBrush(239, 68, 68);
        private readonly IWpfTextView view;
        private readonly CommittedChangeTracker tracker;
        private readonly IWpfTextViewMargin marginContainer;
        private readonly Canvas overlayCanvas;
        private IVerticalScrollBar verticalScrollBar;
        private FrameworkElement verticalScrollBarElement;
        private Panel overlayHostPanel;
        private bool isDisposed;
        private bool isMouseCaptured;

        public CommittedChangeOverviewMargin(IWpfTextView view, CommittedChangeTracker tracker, IWpfTextViewMargin marginContainer)
        {
            this.view = view;
            this.tracker = tracker;
            this.marginContainer = marginContainer;

            Width = 0;
            Height = 0;
            Background = Brushes.Transparent;
            Focusable = false;
            IsHitTestVisible = false;

            overlayCanvas = new Canvas
            {
                Background = Brushes.Transparent,
                ClipToBounds = true,
                SnapsToDevicePixels = true,
                IsHitTestVisible = true,
                Cursor = Cursors.Arrow
            };

            overlayCanvas.MouseLeftButtonDown += OverlayCanvas_MouseLeftButtonDown;
            overlayCanvas.MouseLeftButtonUp += OverlayCanvas_MouseLeftButtonUp;
            overlayCanvas.MouseMove += OverlayCanvas_MouseMove;

            tracker.ChangesUpdated += Tracker_ChangesUpdated;
            view.LayoutChanged += View_LayoutChanged;

            view.VisualElement.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (isDisposed)
                {
                    return;
                }

                TryAttachToVerticalScrollBar();
                UpdateOverlayPlacement();
                Redraw();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public bool Enabled => !isDisposed;

        public double MarginSize => 0;

        public FrameworkElement VisualElement => this;

        public ITextViewMargin GetTextViewMargin(string marginName)
        {
            return string.Equals(marginName, CommittedChangeOverviewMarginProvider.MarginName, StringComparison.Ordinal)
                ? this
                : null;
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            tracker.ChangesUpdated -= Tracker_ChangesUpdated;
            view.LayoutChanged -= View_LayoutChanged;

            overlayCanvas.MouseLeftButtonDown -= OverlayCanvas_MouseLeftButtonDown;
            overlayCanvas.MouseLeftButtonUp -= OverlayCanvas_MouseLeftButtonUp;
            overlayCanvas.MouseMove -= OverlayCanvas_MouseMove;

            if (isMouseCaptured)
            {
                overlayCanvas.ReleaseMouseCapture();
                isMouseCaptured = false;
            }

            DetachFromVerticalScrollBar();
            RemoveOverlayCanvas();
        }

        private void Tracker_ChangesUpdated(object sender, EventArgs e)
        {
            TryAttachToVerticalScrollBar();
            Redraw();
        }

        private void View_LayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            TryAttachToVerticalScrollBar();
            UpdateOverlayPlacement();
            Redraw();
        }

        private void VerticalScrollBar_TrackSpanChanged(object sender, EventArgs e)
        {
            UpdateOverlayPlacement();
            Redraw();
        }

        private void ScrollMap_MappingChanged(object sender, EventArgs e)
        {
            Redraw();
        }

        private void VerticalScrollBarElement_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateOverlayPlacement();
            Redraw();
        }

        private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (isDisposed || verticalScrollBar == null) return;
            overlayCanvas.CaptureMouse();
            isMouseCaptured = true;
            NavigateToOverlayCoordinate(e.GetPosition(overlayCanvas).Y);
            e.Handled = true;
        }

        private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isMouseCaptured) return;
            overlayCanvas.ReleaseMouseCapture();
            isMouseCaptured = false;
            e.Handled = true;
        }

        private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            NavigateToOverlayCoordinate(e.GetPosition(overlayCanvas).Y);
            e.Handled = true;
        }

        private void TryAttachToVerticalScrollBar()
        {
            if (verticalScrollBar != null || marginContainer == null)
            {
                return;
            }

            var resolved = marginContainer.GetTextViewMargin(PredefinedMarginNames.VerticalScrollBar) as IVerticalScrollBar;
            if (resolved == null)
            {
                return;
            }

            var resolvedElement = resolved as FrameworkElement;
            if (resolvedElement == null)
            {
                return;
            }

            var parentPanel = resolvedElement.Parent as Panel;
            if (parentPanel == null)
            {
                return;
            }

            verticalScrollBar = resolved;
            verticalScrollBarElement = resolvedElement;
            overlayHostPanel = parentPanel;

            verticalScrollBar.TrackSpanChanged += VerticalScrollBar_TrackSpanChanged;
            if (verticalScrollBar.Map != null)
            {
                verticalScrollBar.Map.MappingChanged += ScrollMap_MappingChanged;
            }

            verticalScrollBarElement.SizeChanged += VerticalScrollBarElement_SizeChanged;

            AddOverlayCanvas();
            UpdateOverlayPlacement();
            Redraw();
        }

        private void DetachFromVerticalScrollBar()
        {
            if (verticalScrollBar != null)
            {
                verticalScrollBar.TrackSpanChanged -= VerticalScrollBar_TrackSpanChanged;
                if (verticalScrollBar.Map != null)
                {
                    verticalScrollBar.Map.MappingChanged -= ScrollMap_MappingChanged;
                }
            }

            if (verticalScrollBarElement != null)
            {
                verticalScrollBarElement.SizeChanged -= VerticalScrollBarElement_SizeChanged;
            }

            verticalScrollBar = null;
            verticalScrollBarElement = null;
            overlayHostPanel = null;
        }

        private void AddOverlayCanvas()
        {
            if (overlayHostPanel == null || overlayCanvas.Parent == overlayHostPanel)
            {
                return;
            }

            RemoveOverlayCanvas();

            if (overlayHostPanel is Grid grid && verticalScrollBarElement != null)
            {
                Grid.SetRow(overlayCanvas, Grid.GetRow(verticalScrollBarElement));
                Grid.SetColumn(overlayCanvas, Grid.GetColumn(verticalScrollBarElement));
                Grid.SetRowSpan(overlayCanvas, Grid.GetRowSpan(verticalScrollBarElement));
                Grid.SetColumnSpan(overlayCanvas, Grid.GetColumnSpan(verticalScrollBarElement));
            }

            Panel.SetZIndex(overlayCanvas, 1000);
            overlayHostPanel.Children.Add(overlayCanvas);
        }

        private void RemoveOverlayCanvas()
        {
            if (!(overlayCanvas.Parent is Panel parent))
            {
                return;
            }

            parent.Children.Remove(overlayCanvas);
            overlayCanvas.Children.Clear();
        }

        private void UpdateOverlayPlacement()
        {
            if (verticalScrollBarElement == null || overlayHostPanel == null || overlayCanvas.Parent != overlayHostPanel)
            {
                return;
            }

            overlayCanvas.Width = verticalScrollBarElement.ActualWidth;
            overlayCanvas.Height = verticalScrollBarElement.ActualHeight;

            if (overlayHostPanel is Grid)
            {
                overlayCanvas.HorizontalAlignment = HorizontalAlignment.Stretch;
                overlayCanvas.VerticalAlignment = VerticalAlignment.Stretch;
                overlayCanvas.Margin = new Thickness(0);
                overlayCanvas.RenderTransform = Transform.Identity;
                return;
            }

            var origin = verticalScrollBarElement.TranslatePoint(new Point(0, 0), overlayHostPanel);
            overlayCanvas.HorizontalAlignment = HorizontalAlignment.Left;
            overlayCanvas.VerticalAlignment = VerticalAlignment.Top;
            overlayCanvas.Margin = new Thickness(0);
            overlayCanvas.RenderTransform = new TranslateTransform(origin.X, origin.Y);
        }

        private void Redraw()
        {
            overlayCanvas.Children.Clear();

            if (isDisposed || verticalScrollBar == null || verticalScrollBarElement == null)
            {
                return;
            }

            var snapshot = tracker.CurrentSnapshot;
            var changes = tracker.CurrentChanges;
            if (snapshot == null || changes == null || changes.Count == 0)
            {
                return;
            }

            if (overlayCanvas.Height <= 0 || overlayCanvas.Width <= 0)
            {
                return;
            }

            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                if (change.LineNumber < 1 || change.LineNumber > snapshot.LineCount)
                {
                    continue;
                }

                var line = snapshot.GetLineFromLineNumber(change.LineNumber - 1);
                var startY = verticalScrollBar.GetYCoordinateOfBufferPosition(line.Start);
                var endPoint = line.EndIncludingLineBreak > line.Start ? line.EndIncludingLineBreak : line.Start;
                var endY = verticalScrollBar.GetYCoordinateOfBufferPosition(endPoint);
                if (endY < startY)
                {
                    var temp = startY;
                    startY = endY;
                    endY = temp;
                }

                var top = Math.Max(0, startY);
                var bottom = Math.Min(overlayCanvas.Height, Math.Max(top, endY));
                var markerHeight = Math.Max(2.0, bottom - top);
                if (top + markerHeight > overlayCanvas.Height)
                {
                    markerHeight = Math.Max(1.0, overlayCanvas.Height - top);
                }

                if (markerHeight <= 0)
                {
                    continue;
                }

                var markerWidth = Math.Max(2.0, Math.Min(4.0, overlayCanvas.Width * 0.22));
                var markerLeft = Math.Max(0.0, 1.0);
                if (markerLeft + markerWidth > overlayCanvas.Width)
                {
                    markerLeft = Math.Max(0.0, overlayCanvas.Width - markerWidth);
                }

                var marker = new Rectangle
                {
                    Width = markerWidth,
                    Height = markerHeight,
                    Fill = GetBrush(change.Kind),
                    RadiusX = 0.5,
                    RadiusY = 0.5,
                    IsHitTestVisible = false,
                    ToolTip = change.Kind + " at line " + change.LineNumber
                };

                Canvas.SetLeft(marker, markerLeft);
                Canvas.SetTop(marker, top);
                overlayCanvas.Children.Add(marker);
            }
        }

        private void NavigateToOverlayCoordinate(double y)
        {
            if (verticalScrollBar == null || tracker.CurrentSnapshot == null)
            {
                return;
            }

            var clampedY = Math.Max(verticalScrollBar.TrackSpanTop, Math.Min(verticalScrollBar.TrackSpanBottom, y));
            var point = verticalScrollBar.GetBufferPositionOfYCoordinate(clampedY);
            if (point.Snapshot != tracker.CurrentSnapshot)
            {
                return;
            }

            var line = point.GetContainingLine();
            view.Caret.MoveTo(line.Start);
            view.ViewScroller.EnsureSpanVisible(line.Extent, EnsureSpanVisibleOptions.AlwaysCenter);
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
    }
}
