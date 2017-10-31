using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Threading;
using System.ComponentModel;
using System.Collections.ObjectModel;
using GOTSDK;
using GOTSDK.Master;
using GOTSDK.Position;
using System.Diagnostics;
using HelixToolkit.Wpf;
using System.Windows.Media.Media3D;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using GOTSDK.Master.Master2XTypes;
using System.IO.Ports;
using GOTSDKSample.ExternalData;

namespace GOTSDKSample
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window, INotifyPropertyChanged
	{
		public string MasterConnectionStatus { get { return this._masterConnectionStatus; } set { this._masterConnectionStatus = value; OnPropertyChanged("MasterConnectionStatus"); } }
		private string _masterConnectionStatus = "Offline";

		public string MasterVersion { get { return this._masterVersion; } set { this._masterVersion = value; OnPropertyChanged("MasterVersion"); } }
		private string _masterVersion = "Unknown";

		public bool IsMerging
		{
			get
			{
				return this._isMerging;
			}
			set
			{
				this._isMerging = value;
				this.UpdateTransmitterStates();
				OnPropertyChanged("IsMerging");
			}
		}

		private bool _isMerging = false;

		// The currently connected units
		public ObservableCollection<Transmitter> ConnectedTransmitters { get; private set; }
		public ObservableCollection<Receiver> ConnectedReceivers { get; private set; }
		public ObservableCollection<Scenario3D> Scenarios { get; private set; }

		public bool CalibratorTriangleDetected { get { return this._calibratorTriangleDetected; } set { this._calibratorTriangleDetected = value; OnPropertyChanged("CalibratorTriangleDetected"); } }
		private bool _calibratorTriangleDetected = false;

		private Master2X master;

		// Used for calibrating the system 
		private CalibratorTriangle calibratorTriangle;
		private CalibrationWindow calibrationDialog;

		// The latest received measurements
		private Queue<CalculatedPosition> measurementHistory = new Queue<CalculatedPosition>();

		// How many measurement entries to display
		private int HistorySize { get { return this.IsMerging ? 100 : 250; } }

		// The latest calibration is automatically saved to this location. This is just a help for testing/development purposes.
		private static readonly string ScenarioFilePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Calibration.xml");

		private ModelVisual3D currentMeasurementsVisual = new ModelVisual3D();

		// External connections for receiving data. All position data is automatically streamed to these connections.
		private ExternalDataConnection[] externalConnections = new ExternalDataConnection[]
		{
			// Examples on how to create the connections:
			//new TCPDataConnection(portNumber: 26517),
			//new SerialDataConnection(new SerialPort() { PortName = "COM2", BaudRate = 9600, DataBits = 8, Parity = Parity.None, StopBits = StopBits.One, WriteTimeout = 500 })
		};
		
		public MainWindow()
		{
			InitializeComponent();

			// Set application name
			var assemblyName = Assembly.GetExecutingAssembly().GetName();
			this.Title = string.Format("{0} v.{1}.{2}", assemblyName.Name, assemblyName.Version.Major, assemblyName.Version.Minor);

			this.ConnectedTransmitters = new ObservableCollection<Transmitter>();
			this.ConnectedReceivers = new ObservableCollection<Receiver>();
			this.Scenarios = new ObservableCollection<Scenario3D>();
			this.Scenarios.CollectionChanged += delegate
			{
				// Update the display color of scenarios according to their index
				for (int i = 0; i < Scenarios.Count; i++)
					Scenarios[i].DisplayColor = GetScenarioColor(i);
			};

			this.DataContext = this;

			this.master = new Master2X(SynchronizationContext.Current);
			this.master.OnMasterStatusChanged += master_OnMasterStatusChanged;
			this.master.OnNewReceiverConnected += master_OnNewReceiverConnected;
			this.master.OnNewTransmitterConnected += master_OnNewTransmitterConnected;
			this.master.OnMasterInfoReceived += master_OnMasterInfoReceived;
			this.master.OnMeasurementReceived += master_OnMeasurementReceived;

			this.Closed += MainWindow_Closed;

			externalConnections.ForEach(i => i.Start());

			TryOpenLastCalibration();
		}

		// Try to open the last calibration we did in this sample application.
		private void TryOpenLastCalibration()
		{
			if (File.Exists(ScenarioFilePath))
			{
				var doc = XDocument.Load(ScenarioFilePath);
				var loadedScenarios = Scenario3DPersistence.Load(doc);

				foreach (var scenario in loadedScenarios)
					this.Scenarios.Add(scenario);

				ShowScenarios();
			}
		}

		private static SolidColorBrush GetScenarioColor(int index)
		{
			switch (index)
			{
				case 0: return Brushes.Red;
				case 1: return Brushes.Blue;
				case 2: return Brushes.Green;
				case 3: return Brushes.Black;
				case 4: return Brushes.Orange;
				case 5: return Brushes.SlateGray;
				default: return Brushes.White;
			}
		}

		private void MainWindow_Closed(object sender, EventArgs e)
		{
			// Close the Master USB connection
			if (this.master != null)
				this.master.Close();

			externalConnections.ForEach(i => i.Stop());
		}

		private void Button_ConnectToMaster(object sender, RoutedEventArgs e)
		{
			// Try to connect. This will return false immediately if it fails to find the proper USB port.
			// Otherwise, it will begin connecting (async) and the "Master2X.OnMasterStatusChanged" event will be fired soon. 
			if (!this.master.BeginConnect())
			{
				MessageBox.Show("A Master could not be detected. Make sure it is connected and the Silabs USB driver has been installed.", "Failed to detect Master",
					MessageBoxButton.OK,
					MessageBoxImage.Error);
			}
		}

		// Called when a new receiver has been connected to Master
		private void master_OnNewReceiverConnected(Receiver newReceiver)
		{
			if (!this.ConnectedReceivers.Any(r => r.GOTAddress == newReceiver.GOTAddress))
				this.ConnectedReceivers.Add(newReceiver);
		}

		// Called when a new transmitter has been connected to Master. This includes the ones used in the calibrator triangle.
		private void master_OnNewTransmitterConnected(Transmitter newTransmitter)
		{
			if (!this.ConnectedTransmitters.Any(t => t.GOTAddress == newTransmitter.GOTAddress))
			{
				this.ConnectedTransmitters.Add(newTransmitter);
				this.master.SetTransmitterState(newTransmitter.GOTAddress, GetTransmitterState(newTransmitter.GOTAddress), Transmitter.UltraSonicLevel.High);
			}

			// Check if transmitters composing a calibration triangle has been connected.
			if (!this.CalibratorTriangleDetected)
				this.CalibratorTriangleDetected = CalibratorTriangle.TryFindCalibratorTriangle(this.ConnectedTransmitters.Select(s => s.GOTAddress), out this.calibratorTriangle);
		}

		// This is called whenever the master connection status has been changed.
		private void master_OnMasterStatusChanged(IMaster master, MasterStatus newStatus)
		{
			this.MasterConnectionStatus = newStatus.ToString();

			if (newStatus == MasterStatus.Connected)
			{
				// Display the name of the virtual COM port
				this.MasterConnectionStatus += string.Format(" ({0})", master.CurrentPortName);

				// Request firmware version and serial.
				master.RequestMasterInfo();
				
				// The current air temperature
				int temperatureDegrees = 22;
				
				ushort speedOfSoundInMeters = (ushort)(Master2X.GetSpeedOfSoundInMeters(temperatureDegrees) + 0.5); // 0.5 to round to nearest integer value
				((Master2X)master).Setup(110, speedOfSoundInMeters, 16, 0, TPCLINK_ULTRASONIC_LEVEL.LEVEL_4);

				// The very last thing: Request all currently connected units. They will show up in the OnNewTransmitter/OnNewReceiver connected events.
				master.RequestUnits();
			}
		}

		// Called when Master info has been received (master.RequestMasterInfo())
		private void master_OnMasterInfoReceived(IMaster master, string swVersion, string serialNumber)
		{
			this.MasterVersion = string.Format("{0}, Serial: {1}", swVersion, serialNumber);
		}

		private Transmitter.TransmitterState GetTransmitterState(GOTAddress address)
		{
			if (CalibratorTriangle.IsCalibratorTriangleAddress(address))
				return this.IsMerging ? Transmitter.TransmitterState.ActiveHigh : Transmitter.TransmitterState.Deactivated;

			return Transmitter.TransmitterState.ActiveHigh;
		}

		private void UpdateTransmitterStates()
		{
			// Enable the all transmitters in the calibration triangle 
			foreach (var tx in this.ConnectedTransmitters)
				master.SetTransmitterState(tx.GOTAddress, GetTransmitterState(tx.GOTAddress), Transmitter.UltraSonicLevel.High);
		}

		// Called when a measurement is received from the master. In this stage a "measurement" contains distances. We need a scenario for converting the distances into a (X,Y,Z) position.
		private void master_OnMeasurementReceived(Measurement measurement)
		{
			externalConnections.ForEach(i => i.UpdateStatistics(measurement));

			// Are we currently calibrating?
			if (this.calibrationDialog != null)
			{
				this.calibrationDialog.AddNewMeasurement(measurement);
			}
			else // Otherwise try to find a X,Y,Z position.
			{
				if (this.Scenarios.Count == 0)
				{
					externalConnections.ForEach(i => i.SendError(TCPDataConnection.ErrorCode.NoCalibration, measurement.TxAddress));
				}
				else if (measurement.RSSI == 0)
				{
					externalConnections.ForEach(i => i.SendError(TCPDataConnection.ErrorCode.TransmitterRadioLost, measurement.TxAddress));
				}
				else if (measurement.RxMeasurements.Count(dist => dist.Distance > 0) < 3)
				{
					externalConnections.ForEach(i => i.SendError(TCPDataConnection.ErrorCode.NoUltraSound, measurement.TxAddress));
				}

				// Merging is a bit special: Ask all scenarios to provide a position
				if (this.IsMerging)
				{
					foreach (var scenario in this.Scenarios)
					{
						CalculatedPosition pos;
						if (PositionCalculator.TryCalculatePosition(measurement, scenario, out pos))
						{
							this.measurementHistory.Enqueue(pos);
							externalConnections.ForEach(i => i.SendPosition(pos));
						}
						else
						{
							externalConnections.ForEach(i => i.SendError(TCPDataConnection.ErrorCode.Other, measurement.TxAddress));
						}
					}
				}
				else // This is the normal case: Get the best possible position from one scenario.
				{
					CalculatedPosition pos;
					if (PositionCalculator.TryCalculatePosition(measurement, this.Scenarios.ToArray(), out pos))
					{
						this.measurementHistory.Enqueue(pos);
						externalConnections.ForEach(i => i.SendPosition(pos));
					}
					else
					{
						externalConnections.ForEach(i => i.SendError(TCPDataConnection.ErrorCode.Other, measurement.TxAddress));
					}
				}

				while (this.measurementHistory.Count > HistorySize)
					this.measurementHistory.Dequeue();

				UpdateMeasurementsVisual();
				UpdateGrid();
			}
		}

		// Enable/disable all 3 transmitters in the calibration triangle.
		private void SetCalibratorTriangleTransmitterStatus(bool enabled)
		{
			if (this.calibratorTriangle != null)
			{
				foreach (var address in this.calibratorTriangle.TransmitterAddresses)
				{
					this.master.SetTransmitterState(address, enabled ? Transmitter.TransmitterState.ActiveHigh : Transmitter.TransmitterState.Deactivated, Transmitter.UltraSonicLevel.High);
				}
			}
		}

		private void Button_NewScenario(object sender, RoutedEventArgs e)
		{
			if (CalibratorTriangleDetected && this.ConnectedReceivers.Count >= 3)
			{
				// Make sure all transmitters in the calibration triangle are active and they emit the maximum level of ultra sound.
				SetCalibratorTriangleTransmitterStatus(true);

				// Show calibration dialog (and block until it is finished or cancelled)
				calibrationDialog = new CalibrationWindow(this.ConnectedReceivers, this.calibratorTriangle, this.master) { Owner = this };
				calibrationDialog.ShowDialog();

				// Will be null if the user cancelled.
				if (calibrationDialog.CalibratedScenario != null)
				{
					var scenario = calibrationDialog.CalibratedScenario;
					this.Scenarios.Add(scenario);

					SaveScenariosToFile();

					ShowScenarios();
				}

				calibrationDialog = null;

				// Disable the transmitters again. No reason in having them take up space in the measurement loop.
				SetCalibratorTriangleTransmitterStatus(false);
			}
			else
			{
				MessageBox.Show("Need Calibration Triangle and at least three available receivers in order to create a new Scenario.",
								"Not ready yet",
								MessageBoxButton.OK,
								MessageBoxImage.Error);
			}
		}

		private void SaveScenariosToFile()
		{
			var doc = Scenario3DPersistence.Save(this.Scenarios);
			doc.Save(ScenarioFilePath, System.Xml.Linq.SaveOptions.None);
		}

		/// <summary>
		/// Set the current scenario to be displayed.
		/// </summary>
		private void ShowScenarios()
		{
			this.measurementHistory.Clear();
			this.viewPort.Children.Clear();

			this.viewPort.Children.Add(this.light);

			// Draw the scenario(s), including receivers
			for (int i = 0; i < this.Scenarios.Count; i++)
			{
				var scenarioVisual = BuildScenario3DVisual(this.Scenarios[i], i);
				this.viewPort.Children.Add(scenarioVisual);
			}

			UpdateGrid();
		}

		private void UpdateMeasurementsVisual()
		{
			// Create a new mesh (not that clever performance-wise, but doesn't matter in this case)
			this.viewPort.Children.Remove(this.currentMeasurementsVisual);

			ModelVisual3D newModel = null;

			if (this.IsMerging)
				newModel = BuildMeasurementsVisualForMerge(this.measurementHistory.ToArray());
			else
				newModel = BuildMeasurementsVisual(this.measurementHistory.ToArray(), this.Scenarios.ToArray());

			this.viewPort.Children.Add(newModel);
			this.currentMeasurementsVisual = newModel;
		}

		private int gridUpdateCounter = 0;

		/// <summary>
		/// Resize the grid to match the span of the current data.
		/// </summary>
		private void UpdateGrid()
		{
			if (!viewPort.Children.Contains(grid))
				viewPort.Children.Add(grid);

			if (gridUpdateCounter != 0)
				return;

			if (gridUpdateCounter++ > 10)
				gridUpdateCounter = 0;

			double minX, minY, maxX, maxY;
			GetViewPortItemsBBox(out minX, out maxX, out minY, out maxY);

			double xLength = (maxX - minY);
			double yLength = (maxY - minY);

			xLength = xLength += grid.MajorDistance;
			yLength = yLength += grid.MajorDistance;

			var newCenter = new Point3D(minX + (maxX - minX) / 2, minY + (maxY - minY) / 2, -0.1);

			grid.BeginEdit();
			grid.Width = yLength;
			grid.Length = xLength;
			grid.Center = newCenter;
			grid.EndEdit();
		}

		private void GetViewPortItemsBBox(out double xMin, out double xMax, out double yMin, out double yMax)
		{
			// Default to a 4x4 m grid
			xMin = -2000;
			xMax = 2000;
			yMin = -2000;
			yMax = 2000;

			// Extend the grid in case we have items outside that..
			foreach (var item in this.measurementHistory)
			{
				xMin = Math.Min(xMin, item.Position.X);
				xMax = Math.Max(xMin, item.Position.X);

				yMin = Math.Min(yMin, item.Position.Y);
				yMax = Math.Max(yMin, item.Position.Y);
			}
		}

		private void viewPort_Loaded(object sender, RoutedEventArgs e)
		{
			// Some camera tweaks
			this.viewPort.CameraController.CameraRotationMode = CameraRotationMode.Turntable;
			this.viewPort.CameraController.MoveSensitivity = 35;
			this.viewPort.CameraController.LeftRightPanSensitivity = 35;
			this.viewPort.CameraController.UpDownPanSensitivity = 7;
			this.viewPort.CameraMode = CameraMode.WalkAround;
			this.viewPort.CameraController.IsZoomEnabled = false;
		}

		private void viewPort_MouseWheel(object sender, MouseWheelEventArgs e)
		{
			if (viewPort.CameraController == null)
				return;

			// Change camera position in a forward/backward direction with the scrollwheel 
			var delta = 0.01 * viewPort.CameraController.MoveSensitivity * e.Delta;

			// Slow down if close to the track (assume everything is positioned somewhere around Z=0)
			if (viewPort.CameraController.CameraPosition.Z < 1000)
				delta /= 2;

			viewPort.CameraController.AddMoveForce(0, 0, delta);
			e.Handled = true;
		}

		private void Button_RestartMaster(object sender, RoutedEventArgs e)
		{
			if (this.master != null && this.master.Status != MasterStatus.Offline)
			{
				this.master.RequestRestart();
				this.measurementHistory.Clear();

				this.ConnectedReceivers.Clear();
				this.ConnectedTransmitters.Clear();
			}
		}

		private void Button_DeleteScenario(object sender, RoutedEventArgs e)
		{
			var selected = listboxScenario.SelectedItem as Scenario3D;

			if (selected != null)
			{
				this.Scenarios.Remove(selected);
				SaveScenariosToFile();
			}
		}

		// Called when the user clicked "Change" on a scenario 
		private void Button_MergeScenario(object sender, RoutedEventArgs e)
		{
			var selected = ((FrameworkElement)sender).DataContext as Scenario3D;

			if (selected != null)
			{
				if (this.Scenarios.Count == 1)
				{
					MessageBox.Show("At least two scenarios are required for merge to make sense.");
					return;
				}

				if (!this.IsMerging)
					this.IsMerging = true;

				MergeWindow window = new MergeWindow(selected, this.Scenarios) { Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this };
				window.Closed += delegate
				{
					SaveScenariosToFile();
				};

				window.Show();
			}
		}

		#region INotifyPropertyChanged Members

		public event PropertyChangedEventHandler PropertyChanged = delegate { };

		private void OnPropertyChanged(string propertyName)
		{
			this.PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
		}

		#endregion

		#region Model builder helpers

		/// <summary>
		/// Create the visual representation of a 3D scenario. Index is used for creating a slight variation on the model, such that completely overlapping models are still visible.
		/// </summary>
		private static ModelVisual3D BuildScenario3DVisual(Scenario3D scenario, int index)
		{
			const int ReceiverSize = 30;

			var result = new ModelVisual3D();

			var builder = new MeshBuilder(true, true);

			const int Diameter = 4;
			const int ThetaDiv = 4;

			var xZero = new Point3D(scenario.TriangleSize00toX0, 0, 0);
			var xy = scenario.GetCalibrationCornerPosition3D(Calibration3DPosition.PosXY);
			var arrowLength = (scenario.TriangleSize00toX0 + scenario.TriangleSizeX0toXY + scenario.TriangleSizeXYto00) / 12.0;
			var arrowWidth = 10;

			// The calibration triangle
			builder.AddCylinder(new Point3D(), xZero, Diameter, ThetaDiv);
			builder.AddCylinder(xZero, xy, Diameter, ThetaDiv);
			builder.AddCylinder(xy, new Point3D(), Diameter, ThetaDiv);

			var rand = new Random(index);

			// The arrows located at {0, 0}. Vary the arrow lengths a bit depending on index.
			builder.AddArrow(new Point3D(), new Point3D(0, 0, arrowLength + (arrowLength / 10.0) * rand.NextDouble()), arrowWidth, 3, ThetaDiv);
			builder.AddArrow(new Point3D(), new Point3D(0, arrowLength + (arrowLength / 10.0) * rand.NextDouble(), 0), arrowWidth, 3, ThetaDiv);
			builder.AddArrow(new Point3D(), new Point3D(arrowLength + (arrowLength / 10.0) * rand.NextDouble(), 0, 0), arrowWidth, 3, ThetaDiv);

			foreach (var receiver in scenario.Receivers)
			{
				// Add receiver positions as part of the model
				var pos = scenario.GetReceiverPosition(receiver);
				builder.AddBox(pos, ReceiverSize, ReceiverSize, ReceiverSize);
			}

			GeometryModel3D triangleGeometry = new GeometryModel3D();
			triangleGeometry.Geometry = builder.ToMesh(true);
			triangleGeometry.Material = new DiffuseMaterial(scenario.DisplayColor);

			result.Children.Add(new ModelVisual3D() { Content = triangleGeometry });

			foreach (var receiver in scenario.Receivers)
			{
				// Add billboards with receiver addresses
				var pos = scenario.GetReceiverPosition(receiver);
				result.Children.Add(new BillboardTextVisual3D() { Position = Point3D.Add(pos, new Vector3D(0, 0, ReceiverSize)), Text = receiver.Value.ToString() });
			}

			// Bind the model to follow the ScenarioTranform property on the scenario instance.
			var binding = new Binding("ScenarioTransform");
			binding.Source = scenario;
			BindingOperations.SetBinding(result, ModelVisual3D.TransformProperty, binding);

			return result;
		}

		private static ModelVisual3D BuildMeasurementsVisualForMerge(CalculatedPosition[] history)
		{
			if (history.Length == 0)
				return new ModelVisual3D();

			var modelGroup = new Model3DGroup();

			var transmitters = history.Where(h => CalibratorTriangle.IsCalibratorTriangleAddress(h.TxAddress)).Select(c => c.TxAddress).Distinct();

			// One transmitter at the time
			foreach (var transmitter in transmitters)
			{
				// Randomize color. Seed using the transmitter id, such that the color will always be the same.
				var rand = new Random(transmitter.Value);
				var color = new SolidColorBrush(Color.FromRgb((byte)rand.Next(0xff), (byte)rand.Next(0xff), (byte)rand.Next(0xff)));
				var material = new DiffuseMaterial(color);

				var positions = history.Where(c => c.TxAddress == transmitter).Select(c => c.Position).ToArray();

				var connectionBuilder = new MeshBuilder(false, false);
				var triangleStripBuffer = new Point3D[4];

				// Add connections between all positions
				for (int i = 1; i < positions.Length; i++)
				{
					var a = positions[i - 1];
					var b = positions[i];

					var temp = b - a;
					var direction = new Vector3D(temp.X, temp.Y, 0);
					direction.Normalize();
					var vec1 = new Vector3D(-direction.Y, direction.X, 0) * 5;

					triangleStripBuffer[0] = a + vec1;
					triangleStripBuffer[1] = a - vec1;
					triangleStripBuffer[2] = b + vec1;
					triangleStripBuffer[3] = b - vec1;

					connectionBuilder.AddTriangleStrip(triangleStripBuffer);
				}

				var connectionGeometry = new GeometryModel3D() { Material = material, BackMaterial = material };
				connectionGeometry.Geometry = connectionBuilder.ToMesh(true);
				modelGroup.Children.Add(connectionGeometry);

				var pyramidBuilder = new MeshBuilder(false, false);

				// Add a pyramid for each position
				foreach (var position in positions)
					pyramidBuilder.AddPyramid(position, 10, 10);

				var pyramids = new GeometryModel3D() { Material = material };
				pyramids.Geometry = pyramidBuilder.ToMesh(true);
				modelGroup.Children.Add(pyramids);
			}

			return new ModelVisual3D() { Content = modelGroup };
		}

		private static ModelVisual3D BuildMeasurementsVisual(CalculatedPosition[] fullHistory, Scenario[] scenarios)
		{
			// Skip measurements done by the transmitters in the calibration triangle.
			var history = fullHistory.Where(p => !CalibratorTriangle.IsCalibratorTriangleAddress(p.TxAddress)).ToArray();

			if (history.Length == 0)
				return new ModelVisual3D();

			var modelGroup = new Model3DGroup();

			foreach (var scenario in history.Select(s => s.Scenario).Distinct())
			{
				var pyramidBuilder = new MeshBuilder(false, false);

				// Add a pyramid for each position
				foreach (var item in history.Where(p => p.Scenario == scenario))
					pyramidBuilder.AddPyramid(item.Position, 10, 10);

				var pyramids = new GeometryModel3D() { Material = new DiffuseMaterial(scenario.DisplayColor) };
				pyramids.Geometry = pyramidBuilder.ToMesh(true);
				modelGroup.Children.Add(pyramids);
			}

			var connectionBuilder = new MeshBuilder(false, false);
			var triangleStripBuffer = new Point3D[4];

			// Add connections between all positions
			for (int i = 1; i < history.Length; i++)
			{
				var a = history[i - 1];
				var b = history[i];

				var temp = b.Position - a.Position;
				var direction = new Vector3D(temp.X, temp.Y, 0);
				direction.Normalize();
				var vec1 = new Vector3D(-direction.Y, direction.X, 0) * 5;

				triangleStripBuffer[0] = a.Position + vec1;
				triangleStripBuffer[1] = a.Position - vec1;
				triangleStripBuffer[2] = b.Position + vec1;
				triangleStripBuffer[3] = b.Position - vec1;

				connectionBuilder.AddTriangleStrip(triangleStripBuffer);
			}

			var connectionGeometry = new GeometryModel3D() { Material = Materials.Black, BackMaterial = Materials.Black };
			connectionGeometry.Geometry = connectionBuilder.ToMesh(true);
			modelGroup.Children.Add(connectionGeometry);

			return new ModelVisual3D() { Content = modelGroup };
		}

		#endregion
	}
}
