using System.Collections.Generic;
using System.Windows.Media;

namespace KevGitChanges
{
    public class TreeNode
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public List<TreeNode> Children { get; } = new List<TreeNode>();
        public ImageSource Icon { get; set; }
        public int Depth { get; set; }
        public string WStatus { get; set; }
        public string LStatus { get; set; }
        public string RStatus { get; set; }
        public string MStatus { get; set; }
        public string WToolTip { get; set; }
        public string LToolTip { get; set; }
        public string RToolTip { get; set; }
        public string MToolTip { get; set; }
        public Brush WBrush { get; set; }
        public Brush LBrush { get; set; }
        public Brush RBrush { get; set; }
        public Brush MBrush { get; set; }
        public bool IsExpanded { get; set; } = true;
        public bool IsFile => Children.Count == 0 && !string.IsNullOrEmpty(FullPath);
    }
}
