using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;

namespace Utilities.Editor
{
    [CustomPropertyDrawer(typeof(ObjectBinding))]
    public class ObjectBindingDrawer : NameValueBindingDrawer
    {
        protected override BindingListKind Kind => BindingListKind.Object;
    }
}
