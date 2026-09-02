using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;

namespace Utilities.Editor
{
    [CustomPropertyDrawer(typeof(GroupBinding))]
    public class GroupBindingDrawer : NameValueBindingDrawer
    {
        protected override BindingListKind Kind => BindingListKind.Group;
    }
}
