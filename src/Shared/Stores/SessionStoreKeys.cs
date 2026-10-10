using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.AI;

namespace XBag.Agents.Stores.Internal;

/// <summary>Turns an <see cref="AgentSessionStoreKey"/> and an agent into stable storage identifiers.</summary>
internal static class SessionStoreKeys
{
    /// <summary>The partition name Agent Framework's <c>IsolationKeyScopedAgentSessionStore</c> adds (the tenant / user isolation key).</summary>
    public const string IsolationPartition = "isolation";

    /// <summary>
    /// SHA-256 (64 hex digits) over the session id and every partition, length-prefixed so that no two keys collide by
    /// concatenation. <see cref="AgentSessionStoreKey.Partitions"/> is sorted by name (ordinal), so partition order never matters.
    /// </summary>
    public static string Hash(AgentSessionStoreKey key)
    {
        var builder = new StringBuilder();
        Append(builder, 's', key.SessionId);
        if (key.Partitions is not null)
        {
            foreach (KeyValuePair<string, string> partition in key.Partitions.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Append(builder, 'n', partition.Key);
                Append(builder, 'v', partition.Value);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>The partitions as compact JSON sorted by name, or <see langword="null"/> when there are none.</summary>
    public static string? PartitionsJson(AgentSessionStoreKey key)
    {
        if (key.Partitions is not { Count: > 0 } partitions)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> partition in partitions.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteString(partition.Key, partition.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string? IsolationKey(AgentSessionStoreKey key) =>
        key.Partitions is not null && key.Partitions.TryGetValue(IsolationPartition, out string? value) ? value : null;

    /// <summary>
    /// The configured namespace, else the agent's <see cref="AIAgent.Name"/>. The agent's <see cref="AIAgent.Id"/> is never
    /// used: it is random per instance unless set explicitly, so sessions would silently vanish after a restart.
    /// </summary>
    public static string AgentNamespace(string? configured, AIAgent agent, int maxLength)
    {
        string? name = configured ?? agent.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                "Sessions are stored per agent: give the agent a Name, or set AgentNamespace in the session store options.");
        }

        if (name.Length > maxLength)
        {
            throw new InvalidOperationException($"The agent namespace '{name}' is longer than {maxLength} characters.");
        }

        return name;
    }

    private static void Append(StringBuilder builder, char prefix, string value) =>
        builder.Append(prefix).Append(value.Length).Append(':').Append(value).Append('|');
}
