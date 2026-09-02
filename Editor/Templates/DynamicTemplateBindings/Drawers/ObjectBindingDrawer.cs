using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;

namespace Tools.Editor.EditorUtilities
{
    [CustomPropertyDrawer(typeof(ObjectBinding))]
    public class ObjectBindingDrawer : NameValueBindingDrawer
    {
        protected override BindingListKind Kind => BindingListKind.Object;
    }
}
