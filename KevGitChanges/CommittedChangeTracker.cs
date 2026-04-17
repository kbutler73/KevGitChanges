using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace KevGitChanges
{
    internal sealed class CommittedChangeTracker : IDisposable
    {
        private readonly IWpfTextView view;
        private readonly ITextBuffer buffer;
        private readonly ITextDocument document;
        private readonly DispatcherTimer refreshTimer;
        private bool isClosed;
        private int refreshVersion;

        public CommittedChangeTracker(IWpfTextView view, ITextBuffer buffer, ITextDocumentFactoryService documentFactory)
        {
            this.view = view;
            this.buffer = buffer;
            if (documentFactory != null)
            {
                documentFactory.TryGetTextDocument(buffer, out document);
            }

            refreshTimer = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            refreshTimer.Tick += RefreshTimer_Tick;

            buffer.ChangedLowPriority += Buffer_ChangedLowPriority;
            view.GotAggregateFocus += View_GotAggregateFocus;
            view.Closed += View_Closed;
            if (document != null)
            {
                document.FileActionOccurred += Document_FileActionOccurred;
            }

            ScheduleRefresh();
        }

        public event EventHandler ChangesUpdated;

        public IReadOnlyList<CommittedLineChange> CurrentChanges { get; private set; } = Array.Empty<CommittedLineChange>();

        public ITextSnapshot CurrentSnapshot { get; private set; }

        public void ScheduleRefresh()
        {
            if (isClosed || document == null)
            {
                return;
            }

            refreshTimer.Stop();
            refreshTimer.Start();
        }

        public void Dispose()
        {
            if (isClosed) return;
            isClosed = true;
            refreshTimer.Stop();
            refreshTimer.Tick -= RefreshTimer_Tick;
            buffer.ChangedLowPriority -= Buffer_ChangedLowPriority;
            view.GotAggregateFocus -= View_GotAggregateFocus;
            view.Closed -= View_Closed;
            if (document != null)
            {
                document.FileActionOccurred -= Document_FileActionOccurred;
            }
        }

        private void Buffer_ChangedLowPriority(object sender, TextContentChangedEventArgs e)
        {
            ScheduleRefresh();
        }

        private void Document_FileActionOccurred(object sender, TextDocumentFileActionEventArgs e)
        {
            ScheduleRefresh();
        }

        private void View_GotAggregateFocus(object sender, EventArgs e)
        {
            ScheduleRefresh();
        }

        private void View_Closed(object sender, EventArgs e)
        {
            Dispose();
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            refreshTimer.Stop();
            BeginRefresh();
        }

        private void BeginRefresh()
        {
            if (isClosed || document == null)
            {
                return;
            }

            var snapshot = buffer.CurrentSnapshot;
            var filePath = document.FilePath;
            var version = ++refreshVersion;

            System.Threading.Tasks.Task.Run(() =>
                CommittedChangeGitService.GetCommittedLineChanges(filePath, snapshot.LineCount))
                .ContinueWith(task =>
                {
                    view.VisualElement.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (isClosed || version != refreshVersion)
                        {
                            return;
                        }

                        if (buffer.CurrentSnapshot != snapshot)
                        {
                            ScheduleRefresh();
                            return;
                        }

                        CurrentSnapshot = snapshot;
                        CurrentChanges = task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion
                            ? task.Result
                            : Array.Empty<CommittedLineChange>();

                        ChangesUpdated?.Invoke(this, EventArgs.Empty);
                    }));
                });
        }
    }
}
