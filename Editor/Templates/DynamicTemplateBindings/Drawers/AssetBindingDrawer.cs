using SuperPlay.Domino.TemplatesBehavior.Runtime;
using UnityEditor;

namespace Utilities.Editor
{
    [CustomPropertyDrawer(typeof(AssetBinding))]
    public class AssetBindingDrawer : NameValueBindingDrawer
    {
        protected override BindingListKind Kind => BindingListKind.Asset;
    }
}
