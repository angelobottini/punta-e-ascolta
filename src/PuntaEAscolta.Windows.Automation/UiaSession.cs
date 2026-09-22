using UIA = Interop.UIAutomationClient;

namespace PuntaEAscolta.Windows.Automation;

/// <summary>
/// Oggetti COM di UI Automation posseduti dal thread MTA dedicato: automazione (CUIAutomation8 con timeout),
/// walker della vista raw, richiesta di cache e condizioni di ricerca. Va creato e usato su un solo thread.
/// </summary>
internal sealed class UiaSession
{
    public const uint ConnectionTimeoutMs = 1000;
    public const uint TransactionTimeoutMs = 2500;

    public UIA.IUIAutomation Automation { get; }
    public UIA.IUIAutomationTreeWalker RawWalker { get; }

    /// <summary>Richiesta di cache: tutte le proprietà e i pattern che servono, vista raw, solo l'elemento.</summary>
    public UIA.IUIAutomationCacheRequest ElementCache { get; }

    public UIA.IUIAutomationCondition TextCondition { get; }
    public UIA.IUIAutomationCondition ToolTipCondition { get; }
    public UIA.IUIAutomationCondition TextPatternCondition { get; }

    public UiaSession()
    {
        var automation = new UIA.CUIAutomation8();
        var automation2 = (UIA.IUIAutomation2)automation;
        automation2.ConnectionTimeout = ConnectionTimeoutMs;
        automation2.TransactionTimeout = TransactionTimeoutMs;
        automation2.AutoSetFocus = 0; // mai spostare il focus come effetto collaterale
        Automation = automation;
        RawWalker = automation.RawViewWalker;

        var cache = automation.CreateCacheRequest();
        cache.TreeScope = UIA.TreeScope.TreeScope_Element;
        cache.TreeFilter = automation.RawViewCondition;
        cache.AutomationElementMode = UIA.AutomationElementMode.AutomationElementMode_Full;
        foreach (int id in UiaIds.CachedProperties) cache.AddProperty(id);
        foreach (int id in UiaIds.CachedPatterns) cache.AddPattern(id);
        ElementCache = cache;

        TextCondition = automation.CreatePropertyCondition(UiaIds.ControlType, UiaIds.TextControl);
        ToolTipCondition = automation.CreatePropertyCondition(UiaIds.ControlType, UiaIds.ToolTipControl);
        TextPatternCondition = automation.CreatePropertyCondition(UiaIds.IsTextPatternAvailable, true);
    }
}
