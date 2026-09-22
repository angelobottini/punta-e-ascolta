using System.Security.Cryptography;
using System.Text;
using PuntaEAscolta.Core.Abstractions;

namespace PuntaEAscolta.Windows.Audio;

/// <summary>
/// Protezione dei segreti con DPAPI (utente corrente): il testo cifrato resta legato a utente e PC,
/// quindi una cartella portatile copiata altrove richiede di reinserire la chiave.
/// L'entropia aggiuntiva è fissa e serve solo a distinguere i nostri dati da quelli di altre app.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PuntaEAscolta.Segreti.v1");

    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        byte[] plain = Encoding.UTF8.GetBytes(plainText);
        byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        Array.Clear(plain);
        return Convert.ToBase64String(cipher);
    }

    public string? Unprotect(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return null;
        }

        try
        {
            byte[] cipher = Convert.FromBase64String(protectedText.Trim());
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            string result = Encoding.UTF8.GetString(plain);
            Array.Clear(plain);
            return result;
        }
        catch (Exception)
        {
            // Base64 non valido, dato cifrato da un altro utente o PC, dato corrotto: nessun dettaglio nel log
            // per non rischiare di registrare frammenti del segreto.
            return null;
        }
    }
}
