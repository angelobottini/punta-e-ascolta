namespace PuntaEAscolta.Core.Abstractions;

/// <summary>
/// Registro diagnostico minimale. REGOLA: non registrare mai la chiave API. Il testo letto all'utente
/// va registrato solo a livello Debug (disattivato per impostazione predefinita) per riservatezza.
/// </summary>
public interface ILog
{
    bool IsDebugEnabled { get; }
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public bool IsDebugEnabled => false;
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
