using System.Text;

namespace RunnerManager;

/// <summary>
/// Letters of the Latin, Greek and Cyrillic alphabets that look alike (a Cyrillic а and a Latin a), for the warnings in
/// "Add a PC". A name written in one alphabet is never a problem by itself; one that mixes them, or that matches another
/// name once look-alike letters are read as Latin, is warned about.
/// </summary>
public static class LookAlikes
{
    /// <summary>The alphabets whose letters look like each other's.</summary>
    public enum Alphabet { None, Latin, Greek, Cyrillic }

    // Each letter and the Latin letter it looks like, in the same case. Lowercase letters are read before the name is
    // put in capitals, so a Greek ν reads as v and not as the N that its capital looks like.
    private const string From =
        "АВЕКМНОРСТУХЅІЈԚԜҺӀҮ" + "аеорсухѕіјԛԝһӏԁүпгьк" +
        "ΑΒΕΖΗΙΚΜΝΟΡΤΥΧͿ" + "οκνρυιαϳηωχ";
    private const string To =
        "ABEKMHOPCTYXSIJQWHIY" + "aeopcyxsijqwhldynrbk" +
        "ABEZHIKMNOPTYXJ" + "okvpuiajnwx";

    private static readonly Dictionary<char, char> Latin = From.Zip(To).ToDictionary(p => p.First, p => p.Second);

    /// <summary>Which of the three alphabets a letter belongs to; <see cref="Alphabet.None"/> for other characters and scripts.</summary>
    public static Alphabet AlphabetOf(Rune rune)
    {
        if (!Rune.IsLetter(rune)) return Alphabet.None;
        var v = rune.Value;
        if (v is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 0xC0 and <= 0x2AF or >= 0x1D00 and <= 0x1DBF or >= 0x1E00 and <= 0x1EFF
            or >= 0x2C60 and <= 0x2C7F or >= 0xA720 and <= 0xA7FF or >= 0xAB30 and <= 0xAB6F or >= 0xFF21 and <= 0xFF3A or >= 0xFF41 and <= 0xFF5A)
            return Alphabet.Latin;
        if (v is >= 0x370 and <= 0x3FF or >= 0x1F00 and <= 0x1FFF) return Alphabet.Greek;
        if (v is >= 0x400 and <= 0x52F or >= 0x1C80 and <= 0x1C8F or >= 0x2DE0 and <= 0x2DFF or >= 0xA640 and <= 0xA69F) return Alphabet.Cyrillic;
        return Alphabet.None;
    }

    /// <summary>
    /// The alphabets of the three that a name has letters of, in the order Latin, Greek, Cyrillic. Two or more means the
    /// name mixes them. Digits, signs and letters of other scripts (Chinese, Thai) do not count.
    /// </summary>
    public static IReadOnlyList<Alphabet> AlphabetsIn(string name)
        => Normalized(name).EnumerateRunes().Select(AlphabetOf).Where(a => a != Alphabet.None).Distinct().Order().ToList();

    /// <summary>
    /// A name as it looks: in normalization form KC (full-width letters as plain ones), with look-alike Greek and
    /// Cyrillic letters read as Latin, keyed as Runner Manager keys names (<see cref="MachineRelay.Sanitize"/>). Two
    /// names with the same result look the same.
    /// </summary>
    public static string AsItLooks(string name)
    {
        var text = Normalized(name);
        var read = new StringBuilder(text.Length);
        foreach (var c in text) read.Append(Latin.TryGetValue(c, out var latin) ? latin : c);
        return MachineRelay.Sanitize(read.ToString());
    }

    private static string Normalized(string name)
    {
        try { return name.Normalize(NormalizationForm.FormKC); }
        // Not valid Unicode (a lone surrogate, for example): read as it is.
        catch (ArgumentException) { return name; }
    }
}
