using System;

namespace CampaignVault.Authoring.Vault;

public sealed class VaultException : Exception
{
    public VaultException(string message) : base(message)
    {
    }

    public VaultException(string message, Exception innerException) : base(message, innerException)
    {
    }
}