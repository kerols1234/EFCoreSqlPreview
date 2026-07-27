using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace EFCoreSqlPreview.Commands
{
    /// <summary>
    /// Parents the preview command onto the C# editor's right-click menu.
    /// </summary>
    /// <remarks>
    /// <see cref="CommandPlacement.KnownPlacements"/> has no editor-context-menu constant, so the placement is
    /// declared with VSCT ids. The two <c>VsctParent</c> overloads are not interchangeable:
    /// <see cref="CommandPlacement.VsctParent"/> expects a <em>group</em>, while
    /// <see cref="GroupPlacement.VsctParent"/> expects a <em>menu or toolbar</em>. The code window's context
    /// menu is a menu, so it has to be a group of ours parented to it, with the command as a child.
    /// </remarks>
    internal static class EditorContextMenuPlacement
    {
        /// <summary><c>guidSHLMainMenu</c>, the shell's own command set.</summary>
        private static readonly Guid ShellMainMenu = new("d309f791-903f-11d0-9efc-00a0c911004f");

        /// <summary><c>IDM_VS_CTXT_CODEWIN</c>: the code-window right-click menu.</summary>
        private const uint CodeWindowContextMenu = 0x040D;

        /// <summary>The group that carries the preview command on the editor context menu.</summary>
        [VisualStudioContribution]
        public static CommandGroupConfiguration EditorContextGroup =>
            new(GroupPlacement.VsctParent(ShellMainMenu, CodeWindowContextMenu, priority: 0x0100))
            {
                Children = [GroupChild.Command<PreviewSqlCommand>()],
            };
    }
}
