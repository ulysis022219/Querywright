using System;

namespace UlysiSQL.Core
{
    /// <summary>Host-independent decisions for the suggestion list: when it opens, how matches are grouped and which keys insert.</summary>
    public static class CompletionRules
    {
        /// <summary>Punctuation that opens the list like a letter does.</summary>
        public const string ListOperators = ".(,+-*/%=&|^><~:'";

        /// <summary>True when typing <paramref name="typed"/> should open the list. <paramref name="lineToCaret"/> is the line up to
        /// and including the typed character; <paramref name="word"/> is the word under the caret.</summary>
        public static bool OpensList(char typed, string lineToCaret, string word)
        {
            if (char.IsControl(typed) || string.IsNullOrWhiteSpace(lineToCaret)) return false;
            if (!string.IsNullOrEmpty(word) && char.IsDigit(word[0])) return false;
            return char.IsLetter(typed) || typed == '_' || typed == ' ' || ListOperators.IndexOf(typed) >= 0;
        }

        /// <summary>List order group, lowest first: a fully typed name, promoted items (joins), everything else, snippets.</summary>
        public static int Group(MatchKind kind, bool promoted, bool snippet, bool hasTyped) =>
            hasTyped && kind == MatchKind.Exact ? 0 : snippet ? 3 : promoted ? 1 : 2;

        /// <summary>True when a commit key should be typed, not commit: the item's name continues with that very character
        /// (typing "my" then space with "my table" selected).</summary>
        public static bool KeepsTyping(string itemName, string typed, char key) =>
            itemName != null && itemName.StartsWith(typed + key, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the commit key is already the last character inserted, so typing it again would double it:
        /// ( after COUNT(, ] after [My Table], space after text ending in white space. GO never swallows.</summary>
        public static bool SwallowsKey(string inserted, char key)
        {
            if (string.IsNullOrEmpty(inserted) || inserted.Equals("GO", StringComparison.OrdinalIgnoreCase)) return false;
            char last = inserted[inserted.Length - 1];
            return key == '(' ? last == '(' : key == ']' ? last == ']' : key == ' ' && char.IsWhiteSpace(last);
        }
    }
}
