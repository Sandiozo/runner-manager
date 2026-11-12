using System.Text;

namespace RunnerManager;

/// <summary>
/// Letters of the Latin, Greek and Cyrillic alphabets that look alike (a Cyrillic а and a Latin a), for the warnings in
/// "Add a PC". A name written in one alphabet is never a problem by itself; one that mixes alphabets that have letters
/// like Latin ones, or that matches another name once look-alike letters are read as Latin, is warned about.
/// </summary>
public static class LookAlikes
{
    /// <summary>
    /// The alphabets whose letters look like each other's, and <see cref="Other"/> for the letters of the other scripts
    /// that have letters like Latin letters or digits (see <see cref="AlphabetOf"/>).
    /// </summary>
    public enum Alphabet { None, Latin, Greek, Cyrillic, Other }

    // Each letter and the Latin letter or digit it looks like, in the same case. The table is read once as the name is
    // written, so a Greek ν reads as v, and once in capitals, so a Cyrillic н reads as H (see LookAlike). Accented
    // letters are read as their letter and accent, so a Cyrillic Ё reads as Ë.
    private const string From =
        "АВЕКМНОРСТУХЅІЈԚԜҺӀҮ" + "аеорсухѕіјԛԝһӏԁүпгьк" +
        "ΑΒΕΖΗΙΚΜΝΟΡΤΥΧͿ" + "οκνρυιαϳηωχ" +
        "ԌѴЗЬӔӘӨѲӠҪ" + "ѵѡћбӕәөѳӡҫ" +
        "ϜϺϷ" + "γσβϸ";
    private const string To =
        "ABEKMHOPCTYXSIJQWHIY" + "aeopcyxsijqwhldynrbk" +
        "ABEZHIKMNOPTYXJ" + "okvpuiajnwx" +
        "GV3bÆƏƟƟƷÇ" + "vwħ6æəɵɵʒç" +
        "FMÞ" + "yoßþ";

    private static readonly Dictionary<char, char> Latin = From.Zip(To).ToDictionary(p => p.First, p => p.Second);

    /// <summary>
    /// Which alphabet a letter belongs to. <see cref="Alphabet.None"/> for digits, signs and other characters that are no
    /// letters, and for the letters of scripts that have no letter like a Latin one (Chinese, Japanese, Korean, Thai,
    /// Arabic, Hebrew, Devanagari and others): together with Latin letters they cannot pass for a Latin name.
    /// </summary>
    public static Alphabet AlphabetOf(Rune rune)
    {
        if (!Rune.IsLetter(rune)) return Alphabet.None;
        var v = rune.Value;
        if (v is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 0xC0 and <= 0x2FF or >= 0x1D00 and <= 0x1DBF or >= 0x1E00 and <= 0x1EFF
            or >= 0x2C60 and <= 0x2C7F or >= 0xA720 and <= 0xA7FF or >= 0xAB30 and <= 0xAB6F or >= 0xFF21 and <= 0xFF3A or >= 0xFF41 and <= 0xFF5A)
            return Alphabet.Latin;
        if (v is >= 0x370 and <= 0x3FF or >= 0x1F00 and <= 0x1FFF) return Alphabet.Greek;
        if (v is >= 0x400 and <= 0x52F or >= 0x1C80 and <= 0x1C8F or >= 0x2DE0 and <= 0x2DFF or >= 0xA640 and <= 0xA69F) return Alphabet.Cyrillic;
        // Other scripts with letters like Latin letters: Armenian (Oh, like O), Cherokee (Go, like A), Lisu (Pa, like P),
        // Coptic (Ro, like P), Canadian syllabics (Ki, like P), Runic (Berkanan, like B) and Tifinagh (Yar, like O).
        if (v is >= 0x530 and <= 0x58F or >= 0xFB13 and <= 0xFB17 or >= 0x13A0 and <= 0x13FF or >= 0xAB70 and <= 0xABBF
            or >= 0xA4D0 and <= 0xA4FF or >= 0x2C80 and <= 0x2CFF or >= 0x1400 and <= 0x167F or >= 0x18B0 and <= 0x18FF
            or >= 0x16A0 and <= 0x16FF or >= 0x2D30 and <= 0x2D7F)
            return Alphabet.Other;
        return Alphabet.None;
    }

    /// <summary>
    /// The alphabets that a name has letters of, in the order Latin, Greek, Cyrillic, Other. Two or more means the name
    /// mixes them. The letters of the other scripts with letters like Latin ones count as one, <see cref="Alphabet.Other"/>;
    /// digits, signs and the letters of scripts without such letters do not count.
    /// </summary>
    public static IReadOnlyList<Alphabet> AlphabetsIn(string name)
        => Normalized(name, NormalizationForm.FormKC).EnumerateRunes().Select(AlphabetOf).Where(a => a != Alphabet.None).Distinct().Order().ToList();

    /// <summary>
    /// Whether two names look the same: once look-alike Greek and Cyrillic letters are read as Latin, as they are written
    /// (<see cref="AsItLooks"/>) or in capitals (<see cref="AsItLooksInCapitals"/>).
    /// </summary>
    public static bool LookAlike(string a, string b)
    {
        var first = new[] { AsItLooks(a), AsItLooksInCapitals(a) };
        return first.Contains(AsItLooks(b)) || first.Contains(AsItLooksInCapitals(b));
    }

    /// <summary>
    /// A name as it looks: in normalization form KC (full-width letters as plain ones), with look-alike Greek and
    /// Cyrillic letters read as Latin, keyed as Runner Manager keys names (<see cref="MachineRelay.Sanitize"/>).
    /// </summary>
    public static string AsItLooks(string name) => MachineRelay.Sanitize(Read(Lunate(name)));

    /// <summary>
    /// Like <see cref="AsItLooks"/>, with the name put in capitals before the table is read: a Cyrillic н looks like a
    /// small capital H, and its capital Н like H.
    /// </summary>
    public static string AsItLooksInCapitals(string name) => MachineRelay.Sanitize(Read(Lunate(name).ToUpperInvariant()));

    /// <summary>
    /// The Greek lunate sigma looks like C, but normalization form KC makes it a sigma, so it is read before.
    /// </summary>
    private static string Lunate(string name) => name.Replace('Ϲ', 'C').Replace('ϲ', 'c');

    /// <summary>Reads each letter through the table, with accents apart from their letters, and puts the accents back.</summary>
    private static string Read(string name)
    {
        var text = Normalized(name, NormalizationForm.FormKD);
        var read = new StringBuilder(text.Length);
        foreach (var c in text) read.Append(Latin.TryGetValue(c, out var latin) ? latin : c);
        return Normalized(read.ToString(), NormalizationForm.FormC);
    }

    private static string Normalized(string name, NormalizationForm form)
    {
        try { return name.Normalize(form); }
        // Not valid Unicode (a lone surrogate, for example): read as it is.
        catch (ArgumentException) { return name; }
    }
}
