using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SCPBrowser
{
    public partial class LoadingOverlay : UserControl
    {
        private Storyboard? _animationStoryboard;
        private bool _isAnimating = false;

        // Rotating hints shown while the app is busy — turns dead wait time into feature discovery.
        private static readonly string[] Hints =
        {
            "Colour the Explorer by biological condition, predicted cell type, plate, or k-means cluster from the “Color by” menu.",
            "The QScore classifier scores quantitative marker abundance and rank, so it separates cells that presence/absence markers miss.",
            "Guide classification with domain knowledge: define key marker genes, set expected population frequencies, or exclude unlikely cell types.",
            "Utils → Evaluate Classifier runs a cross-validated benchmark of your classifier against the known conditions — nothing is saved to the project.",
            "Utils → Classification Confidence Map draws a per-cell confidence heatmap you can export as a publication figure (HTML + SVG).",
            "Import a DIA-NN gene matrix straight from Excel via Import → Excel Gene Matrix — the cell type is read from each sample’s folder.",
            "Set a minimum protein-count threshold to exclude low-quality cells; sanity-check it against your negative controls.",
            "ComBat batch correction is built in — it removes plate-to-plate effects while preserving biological signal.",
            "Highly-variable-protein filtering before PCA/UMAP focuses the embedding on the proteins that actually vary between cells.",
            "The Cell Plate Viewer shows cellenONE isolation images and flags wells with more than one cell (doublets) so you can exclude them.",
            "Reconcile Cells ↔ Runs matches your isolated cells to their DIA-NN raw files by plate position.",
            "Hover any point in the Explorer to see its full classification scores and marker evidence.",
            "Uncheck rows in Selected Points to drop those cells from downstream analysis and export.",
            "Once the embedding looks right, use marker classification and GO enrichment to interpret each cluster.",
            "Export annotated datasets to PatternLab / BioTessera for differential analysis.",
        };

        private readonly DispatcherTimer _hintTimer;
        private List<int> _hintOrder = new();
        private int _hintPos;
        private static readonly Random _rng = new();

        public LoadingOverlay()
        {
            InitializeComponent();
            CreateAnimations();

            _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _hintTimer.Tick += (_, __) => AdvanceHint();

            // DON'T auto-start animations on load
            // Loaded += LoadingOverlay_Loaded;
        }

        /// <summary>Shuffles the hint order and shows the first one; called each time the overlay appears.</summary>
        private void StartHints()
        {
            if (Hints.Length == 0 || _hintTimer.IsEnabled) return;   // already rotating — don't reset on repeated Show()
            _hintOrder = Enumerable.Range(0, Hints.Length).OrderBy(_ => _rng.Next()).ToList();
            _hintPos = 0;
            HintText.BeginAnimation(OpacityProperty, null);
            HintText.Opacity = 1;
            HintText.Text = Hints[_hintOrder[0]];
            HintPanel.Visibility = Visibility.Visible;
            _hintTimer.Start();
        }

        private void StopHints()
        {
            _hintTimer.Stop();
            HintPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>Fades the current hint out, swaps in the next, fades it back in.</summary>
        private void AdvanceHint()
        {
            if (_hintOrder.Count == 0) return;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.35));
            fadeOut.Completed += (_, __) =>
            {
                _hintPos = (_hintPos + 1) % _hintOrder.Count;
                HintText.Text = Hints[_hintOrder[_hintPos]];
                HintText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.35)));
            };
            HintText.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void CreateAnimations()
        {
            _animationStoryboard = new Storyboard();

            // Outer ring rotation (clockwise)
            var outerRotation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(3),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTargetName(outerRotation, "OuterRotation");
            Storyboard.SetTargetProperty(outerRotation, new PropertyPath(System.Windows.Media.RotateTransform.AngleProperty));
            _animationStoryboard.Children.Add(outerRotation);

            // Inner ring rotation (counter-clockwise)
            var innerRotation = new DoubleAnimation
            {
                From = 360,
                To = 0,
                Duration = TimeSpan.FromSeconds(2),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTargetName(innerRotation, "InnerRotation");
            Storyboard.SetTargetProperty(innerRotation, new PropertyPath(System.Windows.Media.RotateTransform.AngleProperty));
            _animationStoryboard.Children.Add(innerRotation);

            // Logo pulse animation
            var logoPulseX = new DoubleAnimation
            {
                From = 1.0,
                To = 1.1,
                Duration = TimeSpan.FromSeconds(1),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTargetName(logoPulseX, "LogoScale");
            Storyboard.SetTargetProperty(logoPulseX, new PropertyPath(System.Windows.Media.ScaleTransform.ScaleXProperty));
            _animationStoryboard.Children.Add(logoPulseX);

            var logoPulseY = new DoubleAnimation
            {
                From = 1.0,
                To = 1.1,
                Duration = TimeSpan.FromSeconds(1),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTargetName(logoPulseY, "LogoScale");
            Storyboard.SetTargetProperty(logoPulseY, new PropertyPath(System.Windows.Media.ScaleTransform.ScaleYProperty));
            _animationStoryboard.Children.Add(logoPulseY);

            // Dots animation (sequential fade in/out)
            CreateDotAnimation("Dot1", 0.0);
            CreateDotAnimation("Dot2", 0.3);
            CreateDotAnimation("Dot3", 0.6);
        }

        private void CreateDotAnimation(string dotName, double beginTimeSeconds)
        {
            var fadeAnimation = new DoubleAnimation
            {
                From = 0.3,
                To = 1.0,
                Duration = TimeSpan.FromSeconds(0.6),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(beginTimeSeconds)
            };
            Storyboard.SetTargetName(fadeAnimation, dotName);
            Storyboard.SetTargetProperty(fadeAnimation, new PropertyPath(UIElement.OpacityProperty));
            _animationStoryboard.Children.Add(fadeAnimation);
        }

        /// <summary>
        /// Sets the main loading message
        /// </summary>
        public void SetMessage(string message)
        {
            MainMessage.Text = message;
        }

        /// <summary>
        /// Sets the secondary progress message
        /// </summary>
        public void SetProgress(string progressText)
        {
            ProgressMessage.Text = progressText;
        }

        /// <summary>
        /// Shows the loading overlay
        /// </summary>
        public void Show()
        {
            this.Visibility = Visibility.Visible;

            // Only start animation if not already animating
            if (!_isAnimating)
            {
                try
                {
                    _animationStoryboard.Begin(this);
                    _isAnimating = true;
                }
                catch (Exception)
                {

                    // Continue anyway - the overlay will still show
                }
            }

            StartHints();
        }

        /// <summary>
        /// Hides the loading overlay
        /// </summary>
        public void Hide()
        {
            this.Visibility = Visibility.Collapsed;

            StopHints();

            // Stop animation
            if (_isAnimating)
            {
                try
                {
                    _animationStoryboard.Stop(this);
                    _isAnimating = false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LoadingOverlay.Hide animation stop] {ex}");
                }
            }
        }
    }
}