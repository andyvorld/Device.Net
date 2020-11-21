using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Device.Net.Reactive
{
    public delegate Task<IReadOnlyList<ConnectedDeviceDefinition>> GetConnectedDevicesAsync();

    /// <summary>
    /// This class is a work in progress. It is not production ready.
    /// </summary>
    public class ReactiveDeviceManager : IReactiveDeviceManager, IDisposable
    {
        #region Fields
        private readonly ILogger<ReactiveDeviceManager> _logger;
        private readonly Func<IDevice, Task> _initializeDeviceAction;
        private IDevice _selectedDevice;
        private readonly Queue<IRequest> _queuedRequests = new Queue<IRequest>();
        private readonly SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _semaphoreSlim2 = new SemaphoreSlim(1, 1);
        private readonly DeviceNotify _notifyDeviceInitialized;
        private readonly NotifyDeviceException _notifyDeviceException;
        private readonly DevicesNotify _notifyConnectedDevices;
        private bool isDisposed;
        private readonly int _pollMilliseconds;
        private readonly GetConnectedDevicesAsync _getConnectedDevicesAsync;
        private readonly GetDeviceAsync _getDevice;
        #endregion

        #region Public Properties
        /// <summary>
        /// Placeholder. Don't use. This functionality will be injected in
        /// </summary>
        public bool FilterMiddleMessages { get; set; }

        public IObservable<IReadOnlyCollection<ConnectedDeviceDefinition>> ConnectedDevicesObservable { get; }

        public IDevice SelectedDevice
        {
            get => _selectedDevice;
            private set
            {
                _selectedDevice = value;
                _notifyDeviceInitialized(value?.ConnectedDeviceDefinition);
            }
        }
        #endregion

        #region Constructor
        /// <summary>
        /// 
        /// </summary>
        /// <param name="notifyDeviceInitialized">Tells others that the device was initialized</param>
        /// <param name="notifyConnectedDevices">Tells others which devices are connected</param>
        /// <param name="notifyDeviceException"></param>
        /// <param name="initializeDeviceAction"></param>
        /// <param name="getConnectedDevicesAsync"></param>
        /// <param name="getDevice"></param>
        /// <param name="pollMilliseconds"></param>
        /// <param name="loggerFactory"></param>
        public ReactiveDeviceManager(
            DeviceNotify notifyDeviceInitialized,
            DevicesNotify notifyConnectedDevices,
            NotifyDeviceException notifyDeviceException,
            Func<IDevice, Task> initializeDeviceAction,
            GetConnectedDevicesAsync getConnectedDevicesAsync,
            GetDeviceAsync getDevice,
            int pollMilliseconds,
            ILoggerFactory loggerFactory = null)
        {
            _notifyDeviceInitialized = notifyDeviceInitialized ?? throw new ArgumentNullException(nameof(notifyDeviceInitialized));
            _notifyDeviceException = notifyDeviceException ?? throw new ArgumentNullException(nameof(notifyDeviceException));
            _notifyConnectedDevices = notifyConnectedDevices ?? throw new ArgumentNullException(nameof(notifyConnectedDevices));
            _getConnectedDevicesAsync = getConnectedDevicesAsync ?? throw new ArgumentNullException(nameof(getConnectedDevicesAsync));
            _getDevice = getDevice ?? throw new ArgumentNullException(nameof(getDevice));

            _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ReactiveDeviceManager>();

            _initializeDeviceAction = initializeDeviceAction;
            _pollMilliseconds = pollMilliseconds;
        }
        #endregion

        #region Public Methods
        public void Start()
        {
            Task.Run(async () =>
            {
                while (!isDisposed)
                {
                    var devices = await _getConnectedDevicesAsync();
                    _notifyConnectedDevices(devices);
                    await Task.Delay(TimeSpan.FromMilliseconds(_pollMilliseconds));
                }
            });
        }

        /// <summary>
        /// Sets the selected device
        /// </summary>
        /// <param name="connectedDevice"></param>
        public void SelectDevice(DeviceSelectedArgs connectedDevice) => _ = InitializeDeviceAsync(connectedDevice.ConnectedDevice);

        public async Task<TResponse> WriteAndReadAsync<TResponse>(IRequest request, Func<byte[], TResponse> convertFunc)
        {
            if (SelectedDevice == null) return default;

            try
            {
                await _semaphoreSlim.WaitAsync();
                var writeBuffer = request.ToArray();
                var readBuffer = await SelectedDevice.WriteAndReadAsync(writeBuffer);
                return convertFunc != null ? convertFunc(readBuffer) : default;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);

                if (!(ex is IOException)) throw;

                _notifyDeviceException(SelectedDevice?.ConnectedDeviceDefinition, ex);
                //The exception was an IO exception so disconnect the device
                //The listener should reconnect

                SelectedDevice.Dispose();

                SelectedDevice = null;

                throw;
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        public void QueueRequest(IRequest request)
        {
            //If ther is no device selected just eat up the messages
            if (SelectedDevice == null) return;

            if (request == null) throw new ArgumentNullException(nameof(request));

            _queuedRequests.Enqueue(request);
            ProcessQueue();
        }

        private async Task ProcessQueue()
        {
            try
            {
                await _semaphoreSlim2.WaitAsync();

                IRequest mostRecentRequest = null;

                if (_queuedRequests.Count == 0) return;

                if (FilterMiddleMessages)
                {
                    //Eat requests except for the most recent one
                    while (_queuedRequests.Count > 0)
                    {
                        mostRecentRequest = _queuedRequests.Dequeue();
                    }
                }

                await WriteAndReadAsync<object>(mostRecentRequest, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
            finally
            {
                _semaphoreSlim2.Release();
            }
        }
        #endregion

        #region Private Methods
        private async Task InitializeDeviceAsync(ConnectedDeviceDefinition connectedDevice)
        {
            try
            {
                if (connectedDevice == null)
                {
                    _logger.LogInformation("Initialize requested but device was null");
                    SelectedDevice = null;
                    return;
                }

                var device = await _getDevice(connectedDevice);
                await _initializeDeviceAction(device);

                _logger.LogInformation("Device initialized {deviceId}", connectedDevice.DeviceId);
                SelectedDevice = device;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
                _notifyDeviceException(connectedDevice, ex);
                SelectedDevice = null;
            }
        }

        public void Dispose() => isDisposed = true;
        #endregion
    }
}
