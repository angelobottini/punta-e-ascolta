namespace PuntaEAscolta.Windows.Audio;

/// <summary>
/// Voce di Windows (OneCore) installata. <paramref name="Gender"/> è il nome dell'enumerazione WinRT
/// ("Female" o "Male"): chi la mostra all'utente la traduce.
/// </summary>
public sealed record WindowsVoiceInfo(string Id, string DisplayName, string Language, string Gender);
