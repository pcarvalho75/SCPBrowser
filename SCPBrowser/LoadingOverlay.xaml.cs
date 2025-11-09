using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SCPBrowser
{
    public partial class LoadingOverlay : UserControl
    {
        private Storyboard _animationStoryboard;

        public LoadingOverlay()
        {
            InitializeComponent();
            CreateAnimations();
            Loaded += LoadingOverlay_Loaded;
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

        private void LoadingOverlay_Loaded(object sender, RoutedEventArgs e)
        {
            _animationStoryboard.Begin(this);
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
            Visibility = Visibility.Visible;
            _animationStoryboard.Begin(this);
        }

        /// <summary>
        /// Hides the loading overlay
        /// </summary>
        public void Hide()
        {
            Visibility = Visibility.Collapsed;
            _animationStoryboard.Stop(this);
        }
    }
}