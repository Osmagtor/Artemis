﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Artemis.Events;
using Artemis.Models;
using Artemis.Services;
using Artemis.Utilities.GameState;
using Artemis.Utilities.Keyboard;
using Artemis.Utilities.LogitechDll;
using Artemis.ViewModels;
using Caliburn.Micro;
using Ninject;
using Ninject.Extensions.Logging;

namespace Artemis.Managers
{
    /// <summary>
    ///     Contains all the other managers and non-loop related components
    /// </summary>
    public class MainManager : IDisposable
    {
        public delegate void PauseCallbackHandler();

        private readonly IEventAggregator _events;
        private readonly ILogger _logger;

        public MainManager(IEventAggregator events, ILogger logger, LoopManager loopManager,
            KeyboardManager keyboardManager, EffectManager effectManager, ProfileManager profileManager)
        {
            _logger = logger;
            LoopManager = loopManager;
            KeyboardManager = keyboardManager;
            EffectManager = effectManager;
            ProfileManager = profileManager;

            _logger.Info("Intializing MainManager");

            _events = events;

            //DialogService = dialogService;
            KeyboardHook = new KeyboardHook();

            ProcessWorker = new BackgroundWorker {WorkerSupportsCancellation = true};
            ProcessWorker.DoWork += ProcessWorker_DoWork;
            ProcessWorker.RunWorkerCompleted += BackgroundWorkerExceptionCatcher;

            // Process worker will always run (and just do nothing when ProgramEnabled is false)
            ProcessWorker.RunWorkerAsync();

            ProgramEnabled = false;
            Running = false;

            // Create and start the web server
            GameStateWebServer = new GameStateWebServer();
            GameStateWebServer.Start();

            // Start the named pipe
            PipeServer = new PipeServer();
            PipeServer.Start("artemis");

            _logger.Info("Intialized MainManager");
        }

        [Inject]
        public Lazy<ShellViewModel> ShellViewModel { get; set; }

        public LoopManager LoopManager { get; }
        public KeyboardManager KeyboardManager { get; set; }
        public EffectManager EffectManager { get; set; }
        public ProfileManager ProfileManager { get; set; }

        public PipeServer PipeServer { get; set; }
        public BackgroundWorker ProcessWorker { get; set; }
        public KeyboardHook KeyboardHook { get; set; }
        public GameStateWebServer GameStateWebServer { get; set; }
        public bool ProgramEnabled { get; private set; }
        public bool Running { get; private set; }

        public void Dispose()
        {
            _logger.Debug("Shutting down MainManager");
            LoopManager.Stop();
            ProcessWorker.CancelAsync();
            ProcessWorker.CancelAsync();
            GameStateWebServer.Stop();
            PipeServer.Stop();
        }

        /// <summary>
        ///     Loads the last active effect and starts the program
        /// </summary>
        public void EnableProgram()
        {
            _logger.Debug("Enabling program");
            ProgramEnabled = true;
            LoopManager.Start();
            _events.PublishOnUIThread(new ToggleEnabled(ProgramEnabled));
        }

        /// <summary>
        ///     Stops the program
        /// </summary>
        public void DisableProgram()
        {
            _logger.Debug("Disabling program");
            LoopManager.Stop();
            ProgramEnabled = false;
            _events.PublishOnUIThread(new ToggleEnabled(ProgramEnabled));
        }

        private void ProcessWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            while (!ProcessWorker.CancellationPending)
            {
                if (!ProgramEnabled)
                {
                    Thread.Sleep(1000);
                    continue;
                }

                var runningProcesses = Process.GetProcesses();

                // If the currently active effect is a disabled game, get rid of it.
                if (EffectManager.ActiveEffect != null)
                    EffectManager.DisableInactiveGame();

                // If the currently active effect is a no longer running game, get rid of it.
                var activeGame = EffectManager.ActiveEffect as GameModel;
                if (activeGame != null)
                    if (!runningProcesses.Any(p => p.ProcessName == activeGame.ProcessName && p.HasExited == false))
                        EffectManager.DisableGame(activeGame);

                // Look for running games, stopping on the first one that's found.
                var newGame = EffectManager.EnabledGames
                    .FirstOrDefault(
                        g => runningProcesses.Any(p => p.ProcessName == g.ProcessName && p.HasExited == false));

                // If it's not already enabled, do so.
                if (newGame != null && EffectManager.ActiveEffect != newGame)
                    EffectManager.ChangeEffect(newGame);

                Thread.Sleep(1000);
            }
        }

        private void BackgroundWorkerExceptionCatcher(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error == null)
                return;

            _logger.Error(e.Error, "Exception in the BackgroundWorker");
            throw e.Error;
        }
    }
}