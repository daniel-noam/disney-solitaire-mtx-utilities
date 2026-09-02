using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;

namespace Tools.Editor.EditorUtilities
{
    [CustomPropertyDrawer(typeof(AssetBinding))]
    public class AssetBindingDrawer : NameValueBindingDrawer
    {
        protected override BindingListKind Kind => BindingListKind.Asset;
    }
}
