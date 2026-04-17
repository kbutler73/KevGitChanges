using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace KevGitChanges
{
    internal static class CommittedChangeActions
    {
        public static void ShowDiff(string filePath)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var snapshot = CommittedChangeGitService.GetBaseFileSnapshot(filePath);
                if (snapshot == null) return;

                var leftPath = CommittedChangeGitService.WriteTempFile(snapshot.RelativePath, snapshot.BaseRef, snapshot.Content);
                var rightPath = filePath;
                var diffService = Package.GetGlobalService(typeof(SVsDifferenceService));
                if (diffService == null)
                {
                    return;
                }

                var leftLabel = snapshot.BaseRef + " (base)";
                var rightLabel = "Workspace";
                var caption = leftLabel + " vs " + rightLabel;

                try
                {
                    dynamic svc = diffService;
                    svc.OpenComparisonWindow2(leftPath, rightPath, leftLabel, rightLabel, caption, null, null, 0);
                    return;
                }
                catch
                {
                    dynamic svc = diffService;
                    svc.OpenComparisonWindow(leftPath, rightPath, leftLabel, rightLabel, caption, null, null, 0);
                }
            }
            catch
            {
                // ignore
            }
        }

        public static void ShowOldCode(string filePath)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var snapshot = CommittedChangeGitService.GetBaseFileSnapshot(filePath);
                if (snapshot == null) return;

                var tempPath = CommittedChangeGitService.WriteTempFile(snapshot.RelativePath, snapshot.BaseRef, snapshot.Content);
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte != null)
                {
                    dte.ItemOperations.OpenFile(tempPath);
                    return;
                }

                System.Diagnostics.Process.Start(tempPath);
            }
            catch
            {
                // ignore
            }
        }
    }
}
