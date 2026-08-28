using System.IO.Compression;
using System.Text.Json;

namespace Ahtola.Tests.Sqltest;

/*
 * English locale data below:
 * Copyright (c) 2016 cksac
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to inclusion of this copyright and permission
 * notice. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
 */

/// <summary>
/// The English provider arrays used by fake 4.4.0. The compressed data is a
/// mechanical copy of fake's MIT/Apache-2.0 <c>src/locales/mod.rs</c>; keeping
/// the original ordering is required because Turso selects by seeded index.
/// </summary>
internal static class SqltestFakeEnglishData
{
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> Data = new(Load);

    public static string[] FirstNames => Data.Value["NAME_FIRST_NAME"];
    public static string[] LastNames => Data.Value["NAME_LAST_NAME"];
    public static string[] CityPrefixes => Data.Value["ADDRESS_CITY_PREFIX"];
    public static string[] CitySuffixes => Data.Value["ADDRESS_CITY_SUFFIX"];
    public static string[] StreetSuffixes => Data.Value["ADDRESS_STREET_SUFFIX"];
    public static string[] StateAbbreviations => Data.Value["ADDRESS_STATE_ABBR"];
    public static string[] ZipFormats => Data.Value["ADDRESS_ZIP_FORMATS"];
    public static string[] PhoneNumberFormats => Data.Value["PHONE_NUMBER_FORMATS"];
    public static string[] SafeEmailDomains { get; } = ["com", "net", "org"];

    private static IReadOnlyDictionary<string, string[]> Load()
    {
        var compressed = Convert.FromBase64String(CompressedData);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(gzip)
               ?? throw new InvalidDataException("Could not load sqltest fake English locale data.");
    }

    private const string CompressedData =
        """
        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
        """;
}
