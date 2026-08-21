using System;

namespace Extensions.AI.AudioHelpers;

public readonly struct DtmfTone : IEquatable<DtmfTone>
{
    private readonly string _value;
    public char CharValue { get; }

    /// <summary> Initializes a new instance of <see cref="DtmfTone"/>. </summary>
    /// <exception cref="ArgumentNullException"> <paramref name="value"/> is null. </exception>
    public DtmfTone(string value, char charValue)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
        CharValue = charValue;
    }

    private const string ZeroValue = "zero";
    private const string OneValue = "one";
    private const string TwoValue = "two";
    private const string ThreeValue = "three";
    private const string FourValue = "four";
    private const string FiveValue = "five";
    private const string SixValue = "six";
    private const string SevenValue = "seven";
    private const string EightValue = "eight";
    private const string NineValue = "nine";
    private const string AValue = "a";
    private const string BValue = "b";
    private const string CValue = "c";
    private const string DValue = "d";
    private const string PoundValue = "pound";
    private const string AsteriskValue = "asterisk";

    /// <summary> zero. </summary>
    public static DtmfTone Zero { get; } = new DtmfTone(ZeroValue, '0');
    /// <summary> one. </summary>
    public static DtmfTone One { get; } = new DtmfTone(OneValue, '1');
    /// <summary> two. </summary>
    public static DtmfTone Two { get; } = new DtmfTone(TwoValue, '2');
    /// <summary> three. </summary>
    public static DtmfTone Three { get; } = new DtmfTone(ThreeValue, '3');
    /// <summary> four. </summary>
    public static DtmfTone Four { get; } = new DtmfTone(FourValue, '4');
    /// <summary> five. </summary>
    public static DtmfTone Five { get; } = new DtmfTone(FiveValue, '5');
    /// <summary> six. </summary>
    public static DtmfTone Six { get; } = new DtmfTone(SixValue, '6');
    /// <summary> seven. </summary>
    public static DtmfTone Seven { get; } = new DtmfTone(SevenValue, '7');
    /// <summary> eight. </summary>
    public static DtmfTone Eight { get; } = new DtmfTone(EightValue, '8');
    /// <summary> nine. </summary>
    public static DtmfTone Nine { get; } = new DtmfTone(NineValue, '9');
    /// <summary> a. </summary>
    public static DtmfTone A { get; } = new DtmfTone(AValue, 'a');
    /// <summary> b. </summary>
    public static DtmfTone B { get; } = new DtmfTone(BValue, 'b');
    /// <summary> c. </summary>
    public static DtmfTone C { get; } = new DtmfTone(CValue, 'c');
    /// <summary> d. </summary>
    public static DtmfTone D { get; } = new DtmfTone(DValue, 'd');
    /// <summary> pound. </summary>
    public static DtmfTone Pound { get; } = new DtmfTone(PoundValue, '#');
    /// <summary> asterisk. </summary>
    public static DtmfTone Asterisk { get; } = new DtmfTone(AsteriskValue, '*');


    /// <summary> Gets a <see cref="DtmfTone"/> from its character (e.g. '5', '#', '*', 'A'). </summary>
    /// <exception cref="ArgumentOutOfRangeException"> If the character is not a valid DTMF tone. </exception>
    public static DtmfTone FromChar(char value)
    {
        var c = char.IsLetter(value) ? char.ToLowerInvariant(value) : value;

        return c switch
        {
            '0' => Zero,
            '1' => One,
            '2' => Two,
            '3' => Three,
            '4' => Four,
            '5' => Five,
            '6' => Six,
            '7' => Seven,
            '8' => Eight,
            '9' => Nine,
            'a' => A,
            'b' => B,
            'c' => C,
            'd' => D,
            '#' => Pound,
            '*' => Asterisk,
            _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unsupported DTMF tone character '{value}'.")
        };
    }

    /// <summary> Tries to get a <see cref="DtmfTone"/> from its character. </summary>
    public static bool TryFromChar(char value, out DtmfTone tone)
    {
        try
        {
            tone = FromChar(value);
            return true;
        }
        catch
        {
            tone = default;
            return false;
        }
    }

    /// <summary> Gets a <see cref="DtmfTone"/> from its textual value (e.g. "five", "pound", "asterisk"). </summary>
    /// <exception cref="ArgumentNullException"> If <paramref name="value"/> is null. </exception>
    /// <exception cref="ArgumentOutOfRangeException"> If the text is not a valid DTMF tone. </exception>
    public static DtmfTone FromString(string value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        return value.ToLowerInvariant() switch
        {
            ZeroValue => Zero,
            OneValue => One,
            TwoValue => Two,
            ThreeValue => Three,
            FourValue => Four,
            FiveValue => Five,
            SixValue => Six,
            SevenValue => Seven,
            EightValue => Eight,
            NineValue => Nine,
            AValue => A,
            BValue => B,
            CValue => C,
            DValue => D,
            PoundValue => Pound,
            AsteriskValue => Asterisk,
            _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unsupported DTMF tone value '{value}'.")
        };
    }

    /// <summary> Tries to get a <see cref="DtmfTone"/> from its textual value. </summary>
    public static bool TryFromString(string? value, out DtmfTone tone)
    {
        if (value is null)
        {
            tone = default;
            return false;
        }

        try
        {
            tone = FromString(value);
            return true;
        }
        catch
        {
            tone = default;
            return false;
        }
    }

    /// <summary> Determines if two <see cref="DtmfTone"/> values are the same. </summary>
    public static bool operator ==(DtmfTone left, DtmfTone right) => left.Equals(right);

    /// <summary> Determines if two <see cref="DtmfTone"/> values are not the same. </summary>
    public static bool operator !=(DtmfTone left, DtmfTone right) => !left.Equals(right);

    public override bool Equals(object? obj) => obj is DtmfTone other && Equals(other);

    public bool Equals(DtmfTone other) => string.Equals(_value, other._value, StringComparison.InvariantCultureIgnoreCase);

    public override int GetHashCode() => _value is not null ? StringComparer.InvariantCultureIgnoreCase.GetHashCode(_value) : 0;

    public override string ToString() => _value;
}
