namespace AutoExile.Modes
{
    /// <summary>
    /// A bot mode controls high-level behavior: what the bot is trying to accomplish.
    /// Modes compose the shared systems (Navigation, Combat, Loot) to achieve their goals.
    ///
    /// Examples: CampaignMode, MapFarmMode, BlightMode, HeistMode
    /// </summary>
    public interface IBotMode
    {
        string Name { get; }

        /// <summary>
        /// Called once when this mode becomes active.
        /// </summary>
        void OnEnter(BotContext ctx);

        /// <summary>
        /// Called once when switching away from this mode.
        /// </summary>
        void OnExit();

        /// <summary>
        /// Called every game tick while this mode is active and the bot is running.
        /// The mode decides what to do: move, fight, loot, interact, wait, etc.
        /// </summary>
        void Tick(BotContext ctx);

        /// <summary>
        /// Optional: render debug overlay for this mode.
        /// </summary>
        void Render(BotContext ctx) { }
    }
}
