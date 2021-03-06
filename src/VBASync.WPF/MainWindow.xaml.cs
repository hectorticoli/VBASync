﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using VBASync.Localization;
using VBASync.Model;

namespace VBASync.WPF {
    internal partial class MainWindow {
        internal const int CopyrightYear = 2017;
        internal const string SupportUrl = "https://github.com/chelh/VBASync";

        internal static readonly Version Version = new Version(2, 1, 2);

        private readonly MainViewModel _vm;

        private bool _doUpdateIncludeAll = true;
        private ActionType? _lastQuietRefreshAction;
        private string _lastQuietRefreshFilePath;
        private string _lastQuietRefreshFolderPath;

        public MainWindow(Startup startup) {
            InitializeComponent();

            DataContext = _vm = new MainViewModel(startup);
            DataContextChanged += (s, e) => QuietRefreshIfInputsOk();
            _vm.Session.PropertyChanged += (s, e) => QuietRefreshIfInputsOk();
            QuietRefreshIfInputsOk();

            // reach into SessionView because these events will not be translated into PropertyChanged
            // if our data validation blocks them
            SessionCtl.FileBrowseBox.LostFocus += (s, e) => QuietRefreshIfInputsOk();
            SessionCtl.FolderBrowseBox.LostFocus += (s, e) => QuietRefreshIfInputsOk();
            SessionCtl.FileBrowseBox.Drop += (s, e) => QuietRefreshIfInputsOk();
            SessionCtl.FolderBrowseBox.Drop += (s, e) => QuietRefreshIfInputsOk();
        }

        private ISession Session => _vm.Session;

        internal void SettingsMenu_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow(_vm.Settings, s => _vm.Settings = s).ShowDialog();
        }

        private void AboutMenu_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow().ShowDialog();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            CheckAndFixErrors();

            var changes = _vm.Changes;
            var committedChanges = changes?.Where(p => p.Commit).ToList();
            if (committedChanges == null || committedChanges.Count == 0)
            {
                return;
            }

            _vm.ActiveSession.Apply(committedChanges);

            QuietRefreshIfInputsOk();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) {
            Application.Current.Shutdown();
        }

        private void ChangesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
            var sel = (Patch)ChangesGrid.SelectedItem;
            var fileName = sel.ModuleName + (sel.ChangeType != ChangeType.ChangeFormControls ? ModuleProcessing.ExtensionFromType(sel.ModuleType) : ".frx");
            string oldPath;
            string newPath;
            if (Session.Action == ActionType.Extract) {
                oldPath = Path.Combine(Session.FolderPath, fileName);
                newPath = Path.Combine(_vm.ActiveSession.FolderPath, fileName);
            } else {
                oldPath = Path.Combine(_vm.ActiveSession.FolderPath, fileName);
                newPath = Path.Combine(Session.FolderPath, fileName);
            }
            if (sel.ChangeType == ChangeType.ChangeFormControls) {
                Lib.FrxFilesAreDifferent(oldPath, newPath, out var explain);
                MessageBox.Show(explain, VBASyncResources.ExplainFrxTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            } else {
                var diffExePath = Environment.ExpandEnvironmentVariables(_vm.Settings.DiffTool);
                if (!File.Exists(oldPath) || !File.Exists(newPath) || !File.Exists(diffExePath)) {
                    return;
                }
                var p = new Process {
                    StartInfo = new ProcessStartInfo(diffExePath, _vm.Settings.DiffToolParameters.Replace("{OldFile}", oldPath).Replace("{NewFile}", newPath)) {
                        UseShellExecute = false
                    }
                };
                p.Start();
            }
        }

        private void CheckAndFixErrors()
        {
            if (!SessionCtl.DataValidationFaulted)
            {
                return;
            }
            var filePath = SessionCtl.FaultedFilePath;
            var folderPath = SessionCtl.FaultedFolderPath;
            if (!string.IsNullOrEmpty(filePath) && filePath.Length > 2 && !FileOrFolderExists(filePath)
                && filePath.StartsWith("\"") && filePath.EndsWith("\"")
                && FileOrFolderExistsOrIsRooted(filePath.Substring(1, filePath.Length - 2)))
            {
                filePath = filePath.Substring(1, filePath.Length - 2);
                _vm.Session.FilePath = filePath;
            }
            if (!string.IsNullOrEmpty(folderPath) && folderPath.Length > 2 && !FileOrFolderExists(folderPath)
                && folderPath.StartsWith("\"") && folderPath.EndsWith("\"")
                && FileOrFolderExistsOrIsRooted(folderPath.Substring(1, folderPath.Length - 2)))
            {
                folderPath = folderPath.Substring(1, folderPath.Length - 2);
                _vm.Session.FolderPath = folderPath;
            }
            if (!string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(folderPath)
                && !File.Exists(filePath) && !Directory.Exists(folderPath)
                && File.Exists(folderPath) && Directory.Exists(filePath))
            {
                _vm.Session.FolderPath = filePath;
                _vm.Session.FilePath = folderPath;
            }

            bool FileOrFolderExists(string path) => File.Exists(path) || Directory.Exists(path);
            bool FileOrFolderExistsOrIsRooted(string path) => FileOrFolderExists(path) || Path.IsPathRooted(path);
        }

        private void ExitMenu_Click(object sender, RoutedEventArgs e) {
            CancelButton_Click(null, null);
        }

        private void IncludeAllBox_Click(object sender, RoutedEventArgs e)
        {
            var vm = _vm.Changes;
            if (vm == null || IncludeAllBox.IsChecked == null) {
                return;
            }
            try {
                _doUpdateIncludeAll = false;
                foreach (var ch in vm) {
                    ch.Commit = IncludeAllBox.IsChecked.Value;
                }
            } finally {
                _doUpdateIncludeAll = true;
            }
            ChangesGrid.Items.Refresh();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) {
            ApplyButton_Click(null, null);
            Application.Current.Shutdown();
        }

        private void QuietRefreshIfInputsOk() {
            CheckAndFixErrors();
            if (SessionCtl.DataValidationFaulted) {
                _vm.Changes = null;
                ApplyButton.IsEnabled = false;
                return;
            }
            if (_vm.Session.Action == _lastQuietRefreshAction
                && _vm.Session.FilePath == _lastQuietRefreshFilePath
                && _vm.Session.FolderPath == _lastQuietRefreshFolderPath)
            {
                return;
            }
            try {
                RefreshButton_Click(null, null);
                _lastQuietRefreshAction = _vm.Session.Action;
                _lastQuietRefreshFilePath = _vm.Session.FilePath;
                _lastQuietRefreshFolderPath = _vm.Session.FolderPath;
            } catch {
                _vm.Changes = null;
                ApplyButton.IsEnabled = false;
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) {
            try
            {
                CheckAndFixErrors();
                if (SessionCtl.DataValidationFaulted)
                {
                    _vm.Changes = null;
                    return;
                }
                _vm.RefreshActiveSession();

                var changes = new ChangesViewModel(_vm.ActiveSession.GetPatches());
                _vm.Changes = changes;
                foreach (var p in changes)
                {
                    p.CommitChanged += (s2, e2) => UpdateIncludeAllBox();
                }
                UpdateIncludeAllBox();
            }
            finally
            {
                ApplyButton.IsEnabled = _vm.Changes?.Count > 0;
                _lastQuietRefreshAction = null;
                _lastQuietRefreshFilePath = null;
                _lastQuietRefreshFolderPath = null;
            }
        }

        private void UpdateIncludeAllBox() {
            if (!_doUpdateIncludeAll) {
                return;
            }
            var vm = _vm.Changes;
            if (vm.All(p => p.Commit)) {
                IncludeAllBox.IsChecked = true;
            } else if (vm.All(p => !p.Commit)) {
                IncludeAllBox.IsChecked = false;
            } else {
                IncludeAllBox.IsChecked = null;
            }
        }

        private void Window_Closed(object sender, EventArgs e) {
            _vm.ActiveSession?.Dispose();

            string lastSessionPath;
            if (_vm.Settings.Portable)
            {
                var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                lastSessionPath = Path.Combine(exeDir, "LastSession.ini");
            }
            else
            {
                lastSessionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VBA Sync Tool", "LastSession.ini");
                Directory.CreateDirectory(Path.GetDirectoryName(lastSessionPath));
            }
            using (var st = new FileStream(lastSessionPath, FileMode.Create))
            {
                _vm.SaveSession(st, true);
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) {
            QuietRefreshIfInputsOk();

            if (Session.AutoRun) {
                OkButton_Click(null, null);
            }
        }
    }
}
