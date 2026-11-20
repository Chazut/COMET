namespace SAIN.SAINComponent.Classes;

public class SAINActivationClass(SAIN.Components.BotComponent bot)
{
    public bool SAINLayersActive;
    public SAIN.Components.BotComponent Bot => bot;
    public void SetActive(bool botActive) { }
    public void ManualUpdate() { }
}
