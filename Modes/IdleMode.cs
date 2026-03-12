namespace AutoExile.Modes
{
    /// <summary>
    /// Default mode — does nothing. Used when no active mode is set
    /// or when the bot needs to pause without stopping entirely.
    /// </summary>
    public class IdleMode : IBotMode
    {
        public string Name => "Idle";

        public void OnEnter(BotContext ctx)
        {
            ctx.Log("Entering idle mode");
        }

        public void OnExit() { }

        public void Tick(BotContext ctx) { }
    }
}
