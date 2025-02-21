using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using Roslyn.Utilities;
using Microsoft.CodeAnalysis.PooledObjects;

namespace RoslynPad.Roslyn.Completion.Providers;

internal abstract class AbstractReferenceDirectiveCompletionProvider : AbstractDirectivePathCompletionProvider
{
    private static readonly CompletionItemRules s_rules = CompletionItemRules.Create(
        filterCharacterRules: [],
        commitCharacterRules: [CharacterSetModificationRule.Create(CharacterSetModificationKind.Replace, GetCommitCharacters())],
        enterKeyRule: EnterKeyRule.Never,
        selectionBehavior: CompletionItemSelectionBehavior.HardSelection);

    private static readonly char[] s_pathIndicators = ['/', '\\', ':'];

    private static ImmutableArray<char> GetCommitCharacters()
    {
        var builder = ArrayBuilder<char>.GetInstance();

        builder.Add('"');

        if (PathUtilities.IsUnixLikePlatform)
        {
            builder.Add('/');
        }
        else
        {
            builder.Add('/');
            builder.Add('\\');
        }

        builder.Add(',');

        return builder.ToImmutableAndFree();
    }

    protected override async Task ProvideCompletionsAsync(CompletionContext context, string pathThroughLastSlash)
    {
        if (GacFileResolver.IsAvailable && pathThroughLastSlash.IndexOfAny(s_pathIndicators) < 0)
        {
            var gacHelper = new GlobalAssemblyCacheCompletionHelper(s_rules);
            context.AddItems(await gacHelper.GetItemsAsync(pathThroughLastSlash, context.CancellationToken).ConfigureAwait(false));
        }

        if (pathThroughLastSlash.IndexOf(',') < 0)
        {
            var helper = GetFileSystemCompletionHelper(context.Document, Microsoft.CodeAnalysis.Glyph.Assembly, [".dll", ".exe"], s_rules);
            context.AddItems(await helper.GetItemsAsync(pathThroughLastSlash, context.CancellationToken).ConfigureAwait(false));
        }
    }
}
