using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace KevGitChanges
{
    public class KevGitChangesOptionsPage : DialogPage
    {
        [Category("Editor")]
        [DisplayName("Show line markers")]
        [Description("Show committed-change markers in the editor gutter.")]
        [DefaultValue(true)]
        public bool ShowLineMarkers { get; set; } = true;

        [Category("Editor")]
        [DisplayName("Show scrollbar markers")]
        [Description("Show committed-change markers in the editor scrollbar map.")]
        [DefaultValue(true)]
        public bool ShowScrollbarMarkers { get; set; } = true;

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            ToolWindow1Package.NotifyOptionsChanged();
        }
    }
}
