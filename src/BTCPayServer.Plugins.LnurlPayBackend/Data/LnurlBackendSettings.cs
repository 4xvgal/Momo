// Data/LnurlBackendSettings.cs
using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.LnurlPayBackend.Data;

/// <summary>
/// Per-store Lightning Address backend configuration.
/// Stored in dedicated table (not StoreBlob) to avoid serialization conflicts.
/// </summary>
public class LnurlBackendSettings
{
    /// <summary>BTCPayServer StoreData.Id foreign key.</summary>
    [Key]
    public string StoreId { get; set; }

    /// <summary>Lightning Address in "user@domain.tld" format.</summary>
    public string LightningAddress { get; set; }

    public bool Enabled { get; set; }

    /// <summary>Timestamp of last successful LUD-06 validation.</summary>
    public DateTimeOffset? LastValidatedAt { get; set; }

    /// <summary>Set true after registration-time LUD-21 verify support check passes.</summary>
    public bool VerifySupportConfirmed { get; set; }
}
