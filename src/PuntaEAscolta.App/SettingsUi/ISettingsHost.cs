using PuntaEAscolta.App.Hosting;
using PuntaEAscolta.Core.Reading;

namespace PuntaEAscolta.App.SettingsUi;

/// <summary>Ciò che la finestra impostazioni chiede all'app (icona di notifica, oppure anteprima a riga di comando).</summary>
internal interface ISettingsHost
{
    AppServices Services { get; }

    /// <summary>Problemi di attivazione attuali (scorciatoie occupate...), da IInputSource.LastProblems.</summary>
    IReadOnlyList<string> InputProblems { get; }

    bool Paused { get; }

    (ReadOutcome? Outcome, DateTime At) LastOutcome { get; }

    void OpenFolder(string path);

    void OpenLogFolder();
}
