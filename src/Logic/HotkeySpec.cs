// HotkeySpec - a parsed chord: a Win32 modifiers mask plus a virtual-key code.
//
// WHY the parse produces two numbers instead of a string: RegisterHotKey takes
// exactly (modifiers, vk) as one int each, so parsing here and registering there
// means the registration call cannot re-interpret a chord differently from the
// way it was validated. The MOD_* bit values are exported as constants so no
// caller has to hardcode 1/2/4/8 a second time and get one of them wrong.
//
// ToString() is the normalised form: modifiers in a fixed order (Ctrl, Alt,
// Shift, Win - the order decision P1 writes them in) followed by the key. The
// config file's default chord therefore round-trips to the same literal it was
// read from, and two log lines about the same chord are comparable.
//
// TryParse is total. The text comes from a hand-edited file, so every input -
// null, empty, "Ctrl+Banana", a lone "+", a bare "S" - returns false with a null
// output instead of throwing; the caller's fallback is the default chord plus one
// note. A chord must carry at least one modifier (DESIGN.md section 8): a bare
// key parses fine in Win32, and registering it would capture that key
// system-wide, so "a HotkeySpec" means "a chord that is safe to register". The
// (modifiers, virtualKey) constructor is private for the same reason: had it
// stayed public, new HotkeySpec(0, 'S') would be exactly the forbidden chord, and
// the type would only *usually* be safe. TryParse and Default are now the only
// ways to obtain an instance, so the invalid state has no representation at all.
//
// No WPF, no P/Invoke, no I/O: the registration itself is WO-06.
using System;
using System.Globalization;
using System.Text;

namespace SnipVid.Logic
{
    /// <summary>
    /// One hotkey chord: the modifier mask and virtual-key code that
    /// RegisterHotKey expects, plus a normalised textual form.
    /// </summary>
    /// <remarks>
    /// Immutable carrier with no side effects. Every instance comes from
    /// <see cref="TryParse"/> or <see cref="Default"/>: the (modifiers,
    /// virtualKey) constructor is private so that a zero-modifier chord - the one
    /// RegisterHotKey would turn into a system-wide key grab - cannot be built by
    /// any caller of this type. The invariant is a property of the type, not of
    /// the parser, because WO-06 passes both numbers straight through.
    /// </remarks>
    public sealed class HotkeySpec
    {
        /// <summary>Win32 MOD_ALT: the Alt key must be held.</summary>
        public const int ModAlt = 1;

        /// <summary>Win32 MOD_CONTROL: Ctrl (or Control) must be held.</summary>
        public const int ModControl = 2;

        /// <summary>Win32 MOD_SHIFT: Shift must be held.</summary>
        public const int ModShift = 4;

        /// <summary>Win32 MOD_WIN: the Windows key must be held.</summary>
        public const int ModWin = 8;

        /// <summary>Virtual-key code for 'S' (VK_S).</summary>
        private const int VkS = 0x53;

        /// <summary>Virtual-key code for '0' (VK_0); also the lowest digit code.</summary>
        private const int VkDigit0 = 0x30;

        /// <summary>Virtual-key code for '9' (VK_9); also the highest digit code.</summary>
        private const int VkDigit9 = 0x39;

        /// <summary>Virtual-key code for 'A' (VK_A); also the lowest letter code.</summary>
        private const int VkLetterA = 0x41;

        /// <summary>Virtual-key code for 'Z' (VK_Z); also the highest letter code.</summary>
        private const int VkLetterZ = 0x5A;

        /// <summary>Virtual-key code for F1 (VK_F1); also the lowest F-key code.</summary>
        private const int VkF1 = 0x70;

        /// <summary>Virtual-key code for F24 (VK_F24); also the highest F-key code.</summary>
        private const int VkF24 = 0x87;

        /// <summary>
        /// The default chord, Ctrl+Alt+Shift+S (decision P1).
        /// </summary>
        /// <remarks>
        /// Win+Shift+* is reserved by Windows, which is why the default uses
        /// Ctrl/Alt/Shift; WO-06 falls back to Ctrl+Alt+Shift+F9 when another
        /// process already owns this chord.
        /// </remarks>
        public static HotkeySpec Default
        {
            get { return new HotkeySpec(ModControl | ModAlt | ModShift, VkS); }
        }

        /// <summary>Stores a chord's Win32 values verbatim.</summary>
        /// <param name="modifiers">Bitwise combination of the MOD_* constants; never 0.</param>
        /// <param name="virtualKey">Win32 virtual-key code.</param>
        /// <remarks>
        /// Side effects: none. Private on purpose: a public version would let a
        /// caller write new HotkeySpec(0, 0x53), and RegisterHotKey(0, 'S')
        /// captures every S typed anywhere in Windows (DESIGN.md section 8). The
        /// values are still stored without a range check, because the two callers
        /// <see cref="TryParse"/> and <see cref="Default"/> have already rejected
        /// everything this type does not accept.
        /// </remarks>
        private HotkeySpec(int modifiers, int virtualKey)
        {
            Modifiers = modifiers;
            VirtualKey = virtualKey;
        }

        /// <summary>Bitwise combination of <see cref="ModAlt"/>, <see cref="ModControl"/>, <see cref="ModShift"/> and <see cref="ModWin"/>.</summary>
        /// <remarks>
        /// Passed to RegisterHotKey unchanged (WO-06). Never 0: the private
        /// constructor is only reached by <see cref="TryParse"/> and
        /// <see cref="Default"/>, both of which always set at least one bit.
        /// </remarks>
        public int Modifiers { get; private set; }

        /// <summary>Win32 virtual-key code, for example 0x53 for 'S'.</summary>
        /// <remarks>Passed to RegisterHotKey unchanged (WO-06).</remarks>
        public int VirtualKey { get; private set; }

        /// <summary>
        /// Parses a chord such as "Ctrl+Alt+Shift+S".
        /// </summary>
        /// <param name="text">
        /// Chord text: at least one modifier (Ctrl/Control, Alt, Shift, Win - any
        /// order, ignoring case) plus one key (A-Z, 0-9 or F1-F24). Whitespace
        /// around tokens is ignored.
        /// </param>
        /// <param name="spec">The parsed chord on success; null on failure.</param>
        /// <returns>True when the text is a chord this build can register.</returns>
        /// <remarks>
        /// Side effects: none. Never throws for any input, including null. A
        /// repeated modifier is accepted and simply sets its bit twice, because
        /// the mask makes duplication harmless; a second key, an unknown
        /// modifier name, an out-of-range key or a chord with a zero modifier
        /// mask is a failure rather than a guess, so a typo becomes the default
        /// chord plus a startup note (C8, C11).
        /// </remarks>
        public static bool TryParse(string text, out HotkeySpec spec)
        {
            spec = null;
            if (text == null)
            {
                return false;
            }

            int modifiers = 0;
            int virtualKey = 0;
            bool hasKey = false;

            foreach (string part in text.Split('+'))
            {
                string token = part.Trim();
                if (token.Length == 0)
                {
                    return false;
                }

                int modifier = ModifierFor(token);
                if (modifier != 0)
                {
                    modifiers |= modifier;
                    continue;
                }

                if (hasKey)
                {
                    return false;
                }

                int key = KeyCodeFor(token);
                if (key == 0)
                {
                    return false;
                }
                virtualKey = key;
                hasKey = true;
            }

            if (!hasKey)
            {
                return false;
            }

            // A bare key is a legal Win32 chord and that is the danger: registering
            // "S" would capture every S typed anywhere in Windows, so this build
            // only registers chords the user cannot trigger by typing (DESIGN.md
            // section 8). No exception for function keys - Ctrl+F9 is one extra
            // character and a rule with an exception is a rule nobody remembers.
            if (modifiers == 0)
            {
                return false;
            }

            spec = new HotkeySpec(modifiers, virtualKey);
            return true;
        }

        /// <summary>Renders the chord in normalised form.</summary>
        /// <returns>
        /// Modifiers in Ctrl, Alt, Shift, Win order, then the key, joined by '+',
        /// for example "Ctrl+Alt+Shift+S".
        /// </returns>
        /// <remarks>
        /// Side effects: none. A virtual-key code outside the accepted key set is
        /// written as 0xNN rather than throwing, so a log line always exists; the
        /// private constructor makes that branch unreachable today, and it stays
        /// as the defensive answer to a code path that has no other way to fail.
        /// </remarks>
        public override string ToString()
        {
            StringBuilder text = new StringBuilder();
            AppendModifier(text, ModControl, "Ctrl");
            AppendModifier(text, ModAlt, "Alt");
            AppendModifier(text, ModShift, "Shift");
            AppendModifier(text, ModWin, "Win");
            if (text.Length > 0)
            {
                text.Append('+');
            }
            text.Append(KeyText(VirtualKey));
            return text.ToString();
        }

        /// <summary>Maps a modifier token to its Win32 bit.</summary>
        /// <param name="token">One token from the chord, already trimmed.</param>
        /// <returns>The MOD_* bit, or 0 when the token is not a modifier name.</returns>
        /// <remarks>
        /// Side effects: none. "Control" is accepted alongside "Ctrl" because
        /// both spellings appear in Windows documentation and neither is a
        /// reasonable thing to punish; 0 doubles as "not a modifier" because no
        /// real bit is zero.
        /// </remarks>
        private static int ModifierFor(string token)
        {
            if (IsName(token, "Ctrl") || IsName(token, "Control"))
            {
                return ModControl;
            }
            if (IsName(token, "Alt"))
            {
                return ModAlt;
            }
            if (IsName(token, "Shift"))
            {
                return ModShift;
            }
            if (IsName(token, "Win"))
            {
                return ModWin;
            }
            return 0;
        }

        /// <summary>Maps a key token to its Win32 virtual-key code.</summary>
        /// <param name="token">One token from the chord, already trimmed.</param>
        /// <returns>The virtual-key code, or 0 when the token is not a key this build accepts.</returns>
        /// <remarks>
        /// Side effects: none. The accepted set is exactly A-Z, 0-9 and F1-F24
        /// (C5). ASCII letters and digits are already their own virtual-key codes,
        /// and VK_F1 is 0x70 with F24 at 0x87, so the F-range is a fixed offset.
        /// </remarks>
        private static int KeyCodeFor(string token)
        {
            if (token.Length == 1)
            {
                char c = token[0];
                if (c >= 'a' && c <= 'z')
                {
                    // VK codes are the uppercase ASCII codes, so a lowercase
                    // token maps to the same key.
                    return c - 'a' + VkLetterA;
                }
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    return c;
                }
                return 0;
            }

            if (token.Length > 1 && (token[0] == 'f' || token[0] == 'F'))
            {
                int number;
                if (int.TryParse(token.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out number)
                    && number >= 1
                    && number <= 24)
                {
                    return VkF1 + number - 1;
                }
            }

            return 0;
        }

        /// <summary>Appends one modifier name and its separator when the bit is set.</summary>
        /// <param name="text">Builder receiving the text.</param>
        /// <param name="bit">MOD_* bit to test.</param>
        /// <param name="name">Canonical name, for example "Ctrl".</param>
        /// <remarks>Side effects: appends to <paramref name="text"/>.</remarks>
        private void AppendModifier(StringBuilder text, int bit, string name)
        {
            if ((Modifiers & bit) == 0)
            {
                return;
            }
            if (text.Length > 0)
            {
                text.Append('+');
            }
            text.Append(name);
        }

        /// <summary>Renders a virtual-key code as the key name TryParse accepts.</summary>
        /// <param name="virtualKey">Win32 virtual-key code.</param>
        /// <returns>A letter or digit, "F1".."F24", or "0xNN" for anything else.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string KeyText(int virtualKey)
        {
            if (virtualKey >= VkLetterA && virtualKey <= VkLetterZ)
            {
                return ((char)virtualKey).ToString();
            }
            if (virtualKey >= VkDigit0 && virtualKey <= VkDigit9)
            {
                return ((char)virtualKey).ToString();
            }
            if (virtualKey >= VkF1 && virtualKey <= VkF24)
            {
                return "F" + (virtualKey - VkF1 + 1).ToString(CultureInfo.InvariantCulture);
            }
            return "0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture);
        }

        /// <summary>Compares a token to a name ignoring case.</summary>
        /// <param name="left">Token from the chord.</param>
        /// <param name="right">Known name, for example "Alt".</param>
        /// <returns>True when they match ignoring case.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static bool IsName(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
