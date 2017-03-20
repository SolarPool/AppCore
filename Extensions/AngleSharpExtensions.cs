using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Dom.Html;

namespace Ciphernote.Extensions
{
    public static class AngleSharpExtensions
    {
        public static bool HasAncestorElementOfTypes(this INode node, params string[] nodeTypes)
        {
            while (node.Parent != null)
            {
                node = node.Parent;

                if (node.NodeType == NodeType.Element &&
                    nodeTypes.Any(x => x == ((IHtmlElement) node).TagName.ToLower()))
                    return true;
            }

            return false;
        }
    }
}
