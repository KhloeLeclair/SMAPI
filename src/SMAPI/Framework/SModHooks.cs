using System;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Enums;
using StardewModdingAPI.Internal;
using StardewValley;
using StardewValley.Menus;

namespace StardewModdingAPI.Framework
{
    /// <summary>Invokes callbacks for mod hooks provided by the game.</summary>
    internal class SModHooks : ModHooks
    {
        /*********
        ** Fields
        *********/
        /// <summary>A callback to invoke before <see cref="Game1.newDayAfterFade"/> runs.</summary>
        private readonly Action BeforeNewDayAfterFade;

        /// <summary>Writes messages to the console.</summary>
        private readonly IMonitor Monitor;

        /// <summary>A callback to invoke when the load stage changes.</summary>
        private readonly Action<LoadStage> OnStageChanged;

        /// <summary>A callback to invoke when the game starts a render step in the draw loop.</summary>
        private readonly Action<RenderSteps, SpriteBatch> OnRenderingStep;

        /// <summary>A callback to invoke when the game finishes a render step in the draw loop.</summary>
        private readonly Action<RenderSteps, SpriteBatch> OnRenderedStep;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="monitor">Writes messages to the console.</param>
        /// <param name="beforeNewDayAfterFade">A callback to invoke before <see cref="Game1.newDayAfterFade"/> runs.</param>
        /// <param name="onStageChanged">A callback to invoke when the load stage changes.</param>
        /// <param name="onRenderingStep">A callback to invoke when the game starts a render step in the draw loop.</param>
        /// <param name="onRenderedStep">A callback to invoke when the game finishes a render step in the draw loop.</param>
        public SModHooks(IMonitor monitor, Action beforeNewDayAfterFade, Action<LoadStage> onStageChanged, Action<RenderSteps, SpriteBatch> onRenderingStep, Action<RenderSteps, SpriteBatch> onRenderedStep)
        {
            this.Monitor = monitor;
            this.BeforeNewDayAfterFade = beforeNewDayAfterFade;
            this.OnStageChanged = onStageChanged;
            this.OnRenderingStep = onRenderingStep;
            this.OnRenderedStep = onRenderedStep;
        }

        /// <summary>A hook invoked when <see cref="Game1.newDayAfterFade"/> is called.</summary>
        /// <param name="action">The vanilla <see cref="Game1.newDayAfterFade"/> logic.</param>
        public override void OnGame1_NewDayAfterFade(Action action)
        {
            this.BeforeNewDayAfterFade.Invoke();
            action();
        }

        /// <summary>Start an asynchronous task for the game.</summary>
        /// <param name="task">The task to start.</param>
        /// <param name="id">A unique key which identifies the task.</param>
        public override Task StartTask(Task task, string id)
        {
            this.Monitor.Log($"Synchronizing '{id}' task...");
            task.RunSynchronously();
            this.Monitor.Log("   task complete.");
            return task;
        }

        /// <summary>Start an asynchronous task for the game.</summary>
        /// <param name="task">The task to start.</param>
        /// <param name="id">A unique key which identifies the task.</param>
        public override Task<T> StartTask<T>(Task<T> task, string id)
        {
            this.Monitor.Log($"Synchronizing '{id}' task...");
            task.RunSynchronously();
            this.Monitor.Log("   task complete.");
            return task;
        }

        /// <summary>A hook invoked when creating a new save slot, after the game has added the location instances but before it fully initializes them.</summary>
        public override void CreatedInitialLocations()
        {
            this.OnStageChanged(LoadStage.CreatedInitialLocations);
        }

        /// <summary>A hook invoked when loading a save slot, after the game has added the location instances but before it restores their save data. Not applicable when connecting to a multiplayer host.</summary>
        public override void SaveAddedLocations()
        {
            this.OnStageChanged(LoadStage.SaveAddedLocations);
        }

        /// <summary>A hook invoked when the game starts a render step in the draw loop.</summary>
        /// <param name="step">The render step being started.</param>
        /// <param name="spriteBatch">The sprite batch being drawn (which might not always be open yet).</param>
        /// <param name="gameTime">A snapshot of the game timing state.</param>
        /// <param name="targetScreen">The render target, if any.</param>
        /// <returns>Returns whether to continue with the render step.</returns>
        public override bool OnRendering(RenderSteps step, SpriteBatch spriteBatch, GameTime gameTime, RenderTarget2D targetScreen)
        {
            this.OnRenderingStep(step, spriteBatch);

            return true;
        }

        /// <summary>A hook invoked when the game starts a render step in the draw loop.</summary>
        /// <param name="step">The render step being started.</param>
        /// <param name="spriteBatch">The sprite batch being drawn (which might not always be open yet).</param>
        /// <param name="gameTime">A snapshot of the game timing state.</param>
        /// <param name="targetScreen">The render target, if any.</param>
        /// <returns>Returns whether to continue with the render step.</returns>
        public override void OnRendered(RenderSteps step, SpriteBatch spriteBatch, GameTime gameTime, RenderTarget2D targetScreen)
        {
            this.OnRenderedStep(step, spriteBatch);
        }

        /// <summary>Draw a menu (or child menu) if possible.</summary>
        /// <param name="menu">The menu to draw.</param>
        /// <param name="drawMenu">The action which draws the menu.</param>
        /// <returns>Returns whether the menu was successfully drawn.</returns>
        public override bool TryDrawMenu(IClickableMenu menu, Action drawMenu)
        {
            try
            {
                drawMenu();
                return true;
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"The {menu.GetMenuChainLabel()} menu crashed while drawing itself. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                Game1.activeClickableMenu.exitThisMenu();
                return false;
            }
        }
    }
}
