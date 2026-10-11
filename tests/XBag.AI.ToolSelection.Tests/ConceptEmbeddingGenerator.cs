using System.Globalization;
using System.Text;

namespace XBag.AI.ToolSelection.Tests;

/// <summary>
/// A deterministic stand-in for an embedding model: one dimension per concept, valued by how many of the concept's words
/// occur in the text. "şemsiye" and "hava durumu" share the weather dimension without sharing a word, which is the point of
/// embeddings. No network, no model download.
/// </summary>
internal sealed class ConceptEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly string[][] Concepts =
    [
        ["hava", "yagmur", "semsiye", "sicaklik", "ruzgar", "derece"],
        ["doviz", "kur", "dolar", "euro", "lira", "para"],
        ["trafik", "kopru", "otoyol"],
        ["kargo", "paket", "gonderi"],
        ["namaz", "ezan", "vakit"],
        ["eczane", "ilac"],
        ["ucak", "bilet", "ucus"],
        ["fatura", "elektrik", "dogalgaz"],
        ["maas", "brut"],
        ["deprem", "sarsinti", "buyukluk"],
        ["mac", "skor", "lig", "gol"],
        ["kullanici", "giris"],
    ];

    /// <summary>Gets every batch this generator received, in order.</summary>
    public List<string[]> Batches { get; } = [];

    /// <summary>Gets or sets a value indicating whether to return one embedding too few (a broken provider).</summary>
    public bool DropOne { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        string[] batch = values.ToArray();
        lock (Batches)
        {
            Batches.Add(batch);
        }

        IEnumerable<string> embedded = DropOne ? batch.Skip(1) : batch;
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embedded.Select(text => new Embedding<float>(Vector(text)))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static float[] Vector(string text)
    {
        string folded = Fold(text);
        return Concepts.Select(words => (float)words.Sum(word => Occurrences(folded, word))).ToArray();
    }

    private static int Occurrences(string text, string word)
    {
        int count = 0;
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string Fold(string text)
    {
        var folded = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                char lower = char.ToLowerInvariant(c);
                folded.Append(lower == 'ı' ? 'i' : lower);
            }
        }

        return folded.ToString();
    }
}
