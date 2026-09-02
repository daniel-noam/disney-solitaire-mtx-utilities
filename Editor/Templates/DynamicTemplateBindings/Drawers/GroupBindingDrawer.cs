using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;

namespace Tools.Editor.EditorUtilities
{
    [CustomPropertyDrawer(typeof(GroupBinding))]
    public class GroupBindingDrawer : NameValueBindingDrawer
    {
        protected override BindingListKind Kind => BindingListKind.Group;
    }
}
