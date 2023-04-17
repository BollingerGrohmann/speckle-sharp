using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Speckle.Core.Credentials;
using Speckle.Core.Helpers;
using Speckle.Core.Logging;
using Speckle.Newtonsoft.Json;
using Timer = System.Timers.Timer;

namespace Speckle.Core.Transports;

/// <summary>
/// Sends data to a speckle server.
/// </summary>
public class ServerTransportV1 : IDisposable, ICloneable, ITransport
{
  private int DOWNLOAD_BATCH_SIZE = 1000;

  private bool IS_WRITING;

  private int MAX_BUFFER_SIZE = 1_000_000;

  private int MAX_MULTIPART_COUNT = 50;

  private ConcurrentQueue<(string, string, int)> Queue = new();

  private int TotalElapsed,
    PollInterval = 100;

  private Timer WriteTimer;

  public ServerTransportV1(Account account, string streamId, int timeoutSeconds = 60)
  {
    Account = account;
    Initialize(account.serverInfo.url, streamId, account.token, timeoutSeconds);
  }

  public string BaseUri { get; private set; }

  public string StreamId { get; set; }

  private HttpClient Client { get; set; }

  public bool CompressPayloads { get; set; } = true;

  public int TotalSentBytes { get; set; }

  public Account Account { get; set; }

  public object Clone()
  {
    return new ServerTransport(Account, StreamId)
    {
      OnErrorAction = OnErrorAction,
      OnProgressAction = OnProgressAction,
      CancellationToken = CancellationToken
    };
  }

  public void Dispose()
  {
    // TODO: check if it's writing first?
    Client?.Dispose();
    WriteTimer.Dispose();
  }

  public string TransportName { get; set; } = "RemoteTransport";

  public Dictionary<string, object> TransportContext =>
    new()
    {
      { "name", TransportName },
      { "type", GetType().Name },
      { "streamId", StreamId },
      { "serverUrl", BaseUri }
    };

  public CancellationToken CancellationToken { get; set; }

  public int SavedObjectCount { get; private set; }

  public Action<string, int> OnProgressAction { get; set; }

  public Action<string, Exception> OnErrorAction { get; set; }

  // not implementing this for V1, just a dummy 0 value
  public TimeSpan Elapsed => TimeSpan.Zero;

  public void BeginWrite()
  {
    if (!GetWriteCompletionStatus())
      throw new SpeckleException("Transport is still writing.");
    TotalSentBytes = 0;
    SavedObjectCount = 0;
  }

  public void EndWrite() { }

  public async Task<Dictionary<string, bool>> HasObjects(List<string> objectIds)
  {
    var payload = new Dictionary<string, string> { { "objects", JsonConvert.SerializeObject(objectIds) } };
    var uri = new Uri($"/api/diff/{StreamId}", UriKind.Relative);
    var response = await Client
      .PostAsync(
        uri,
        new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"),
        CancellationToken
      )
      .ConfigureAwait(false);
    response.EnsureSuccessStatusCode();

    var hasObjectsJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    var hasObjects = JsonConvert.DeserializeObject<Dictionary<string, bool>>(hasObjectsJson);
    return hasObjects;
  }

  private void Initialize(string baseUri, string streamId, string authorizationToken, int timeoutSeconds = 60)
  {
    SpeckleLog.Logger.Information("Initializing New Remote V1 Transport for {baseUri}", baseUri);

    BaseUri = baseUri;
    StreamId = streamId;

    Client = Http.GetHttpProxyClient(
      new SpeckleHttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip }
    );

    Client.BaseAddress = new Uri(baseUri);
    Client.Timeout = new TimeSpan(0, 0, timeoutSeconds);

    if (authorizationToken.ToLowerInvariant().Contains("bearer"))
      Client.DefaultRequestHeaders.Add("Authorization", authorizationToken);
    else
      Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {authorizationToken}");
    WriteTimer = new Timer
    {
      AutoReset = true,
      Enabled = false,
      Interval = PollInterval
    };
    WriteTimer.Elapsed += WriteTimerElapsed;
  }

  public override string ToString()
  {
    return $"Server Transport @{Account.serverInfo.url}";
  }

  internal class Placeholder
  {
    public Dictionary<string, int> __closure { get; set; } = new();
  }

  #region Writing objects

  public async Task WriteComplete()
  {
    await Utilities
      .WaitUntil(
        () =>
        {
          return GetWriteCompletionStatus();
        },
        50
      )
      .ConfigureAwait(false);
  }

  public bool GetWriteCompletionStatus()
  {
    return Queue.Count == 0 && !IS_WRITING;
  }

  private void WriteTimerElapsed(object sender, ElapsedEventArgs e)
  {
    TotalElapsed += PollInterval;

    if (CancellationToken.IsCancellationRequested)
    {
      Queue = new ConcurrentQueue<(string, string, int)>();
      IS_WRITING = false;
      return;
    }

    if (TotalElapsed > 300 && IS_WRITING == false && Queue.Count != 0)
    {
      TotalElapsed = 0;
      WriteTimer.Enabled = false;
#pragma warning disable CS4014
      ConsumeQueue();
#pragma warning restore CS4014
    }
  }

  /// <summary>
  /// Consumes a batch of objects from Queue, of MAX_BUFFER_SIZE or until queue is empty, and filters out the objects that already exist on the server
  /// </summary>
  /// <returns>
  /// Tuple of:
  ///  - int: the number of objects consumed from the queue (useful to report progress)
  ///  - List<(string, string, int)>: List of queued objects that are not already on the server
  /// </returns>
  private async Task<(int, List<(string, string, int)>)> ConsumeNewBatch()
  {
    // Read a batch from the queue

    List<(string, string, int)> queuedBatch = new();
    List<string> queuedBatchIds = new();
    ValueTuple<string, string, int> queueElement;
    var payloadBufferSize = 0;
    while (Queue.TryPeek(out queueElement) && payloadBufferSize < MAX_BUFFER_SIZE)
    {
      if (CancellationToken.IsCancellationRequested)
        return (queuedBatch.Count, null);

      Queue.TryDequeue(out queueElement);
      queuedBatch.Add(queueElement);
      queuedBatchIds.Add(queueElement.Item1);
      payloadBufferSize += queueElement.Item3;
    }

    // Ask the server which objects from the batch it already has
    Dictionary<string, bool> hasObjects = null;
    try
    {
      hasObjects = await HasObjects(queuedBatchIds).ConfigureAwait(false);
    }
    catch (Exception e)
    {
      OnErrorAction?.Invoke(TransportName, e);
      return (queuedBatch.Count, null);
    }

    // Filter the queued batch to only return new objects

    List<(string, string, int)> newBatch = new();
    foreach (var queuedItem in queuedBatch)
      if (!hasObjects.ContainsKey(queuedItem.Item1) || !hasObjects[queuedItem.Item1])
        newBatch.Add(queuedItem);

    return (queuedBatch.Count, newBatch);
  }

  private async Task ConsumeQueue()
  {
    if (CancellationToken.IsCancellationRequested)
    {
      Queue = new ConcurrentQueue<(string, string, int)>();
      IS_WRITING = false;
      return;
    }

    if (Queue.Count == 0)
      return;

    IS_WRITING = true;
    var message = new HttpRequestMessage
    {
      RequestUri = new Uri($"/objects/{StreamId}", UriKind.Relative),
      Method = HttpMethod.Post
    };

    var multipart = new MultipartFormDataContent("--obj--");

    SavedObjectCount = 0;
    var addedMpCount = 0;

    while (addedMpCount < MAX_MULTIPART_COUNT && Queue.Count != 0)
    {
      if (CancellationToken.IsCancellationRequested)
      {
        Queue = new ConcurrentQueue<(string, string, int)>();
        IS_WRITING = false;
        return;
      }

      (int consumedQueuedObjects, List<(string, string, int)> batch) = await ConsumeNewBatch().ConfigureAwait(false);
      if (batch == null)
      {
        // Canceled or error happened (which was already reported)
        Queue = new ConcurrentQueue<(string, string, int)>();
        IS_WRITING = false;
        return;
      }

      if (batch.Count == 0)
      {
        // The server already has all objects from the queued batch
        SavedObjectCount += consumedQueuedObjects;
        continue;
      }

      var _ctBuilder = new StringBuilder("[");
      for (int i = 0; i < batch.Count; i++)
      {
        if (i > 0)
          _ctBuilder.Append(",");
        _ctBuilder.Append(batch[i].Item2);
        TotalSentBytes += batch[i].Item3;
      }
      _ctBuilder.Append("]");
      string _ct = _ctBuilder.ToString();

      if (CompressPayloads)
      {
        var content = new GzipContent(new StringContent(_ct, Encoding.UTF8));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        multipart.Add(content, $"batch-{addedMpCount}", $"batch-{addedMpCount}");
      }
      else
      {
        multipart.Add(new StringContent(_ct, Encoding.UTF8), $"batch-{addedMpCount}", $"batch-{addedMpCount}");
      }

      addedMpCount++;
      SavedObjectCount += consumedQueuedObjects;
    }

    message.Content = multipart;

    if (CancellationToken.IsCancellationRequested)
    {
      Queue = new ConcurrentQueue<(string, string, int)>();
      IS_WRITING = false;
      return;
    }

    if (addedMpCount > 0)
      try
      {
        var response = await Client.SendAsync(message, CancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
      }
      catch (Exception e)
      {
        IS_WRITING = false;
        OnErrorAction?.Invoke(
          TransportName,
          new Exception($"Remote error: {Account.serverInfo.url} is not reachable. \n {e.Message}", e)
        );

        Queue = new ConcurrentQueue<(string, string, int)>();
        return;
      }

    IS_WRITING = false;

    OnProgressAction?.Invoke(TransportName, SavedObjectCount);

    if (!WriteTimer.Enabled)
    {
      WriteTimer.Enabled = true;
      WriteTimer.Start();
    }
  }

  public void SaveObject(string hash, string serializedObject)
  {
    if (CancellationToken.IsCancellationRequested)
    {
      Queue = new ConcurrentQueue<(string, string, int)>();
      IS_WRITING = false;
      return;
    }

    Queue.Enqueue((hash, serializedObject, Encoding.UTF8.GetByteCount(serializedObject)));

    if (!WriteTimer.Enabled && !IS_WRITING)
    {
      WriteTimer.Enabled = true;
      WriteTimer.Start();
    }
  }

  public void SaveObject(string hash, ITransport sourceTransport)
  {
    if (CancellationToken.IsCancellationRequested)
    {
      Queue = new ConcurrentQueue<(string, string, int)>();
      IS_WRITING = false;
      return;
    }

    var serializedObject = sourceTransport.GetObject(hash);

    Queue.Enqueue((hash, serializedObject, Encoding.UTF8.GetByteCount(serializedObject)));

    if (!WriteTimer.Enabled && !IS_WRITING)
    {
      WriteTimer.Enabled = true;
      WriteTimer.Start();
    }
  }

  #endregion

  #region Getting objects

  public string GetObject(string hash)
  {
    if (CancellationToken.IsCancellationRequested)
    {
      Queue = new ConcurrentQueue<(string, string, int)>();
      return null;
    }

    var message = new HttpRequestMessage
    {
      RequestUri = new Uri($"/objects/{StreamId}/{hash}/single", UriKind.Relative),
      Method = HttpMethod.Get
    };

    var response = Client
      .SendAsync(message, HttpCompletionOption.ResponseContentRead, CancellationToken)
      .Result.Content;
    return response.ReadAsStringAsync().Result;
  }

  public async Task<string> CopyObjectAndChildren(
    string hash,
    ITransport targetTransport,
    Action<int> onTotalChildrenCountKnown
  )
  {
    if (CancellationToken.IsCancellationRequested)
    {
      Queue = new ConcurrentQueue<(string, string, int)>();
      return null;
    }

    // Get root object
    var rootHttpMessage = new HttpRequestMessage
    {
      RequestUri = new Uri($"/objects/{StreamId}/{hash}/single", UriKind.Relative),
      Method = HttpMethod.Get
    };

    HttpResponseMessage rootHttpResponse = null;
    try
    {
      rootHttpResponse = await Client
        .SendAsync(rootHttpMessage, HttpCompletionOption.ResponseContentRead, CancellationToken)
        .ConfigureAwait(false);
      rootHttpResponse.EnsureSuccessStatusCode();
    }
    catch (Exception e)
    {
      OnErrorAction?.Invoke(TransportName, e);
      return null;
    }

    string rootObjectStr = await rootHttpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
    List<string> childrenIds = new();
    var rootPartial = JsonConvert.DeserializeObject<Placeholder>(rootObjectStr);
    if (rootPartial.__closure != null)
      childrenIds = new List<string>(rootPartial.__closure.Keys);
    onTotalChildrenCountKnown?.Invoke(childrenIds.Count);

    var childrenFoundMap = await targetTransport.HasObjects(childrenIds).ConfigureAwait(false);
    List<string> newChildrenIds = new(from objId in childrenFoundMap.Keys where !childrenFoundMap[objId] select objId);

    targetTransport.BeginWrite();

    // Get the children that are not already in the targetTransport
    List<string> childrenIdBatch = new(DOWNLOAD_BATCH_SIZE);
    bool downloadBatchResult;
    foreach (var objectId in newChildrenIds)
    {
      childrenIdBatch.Add(objectId);
      if (childrenIdBatch.Count >= DOWNLOAD_BATCH_SIZE)
      {
        downloadBatchResult = await CopyObjects(childrenIdBatch, targetTransport).ConfigureAwait(false);
        if (!downloadBatchResult)
          return null;
        childrenIdBatch = new List<string>(DOWNLOAD_BATCH_SIZE);
      }
    }
    if (childrenIdBatch.Count > 0)
    {
      downloadBatchResult = await CopyObjects(childrenIdBatch, targetTransport).ConfigureAwait(false);
      if (!downloadBatchResult)
        return null;
    }

    targetTransport.SaveObject(hash, rootObjectStr);
    await targetTransport.WriteComplete().ConfigureAwait(false);
    return rootObjectStr;
  }

  private async Task<bool> CopyObjects(List<string> hashes, ITransport targetTransport)
  {
    Stream childrenStream = null;

    if (hashes.Count > 0)
    {
      var childrenHttpMessage = new HttpRequestMessage
      {
        RequestUri = new Uri($"/api/getobjects/{StreamId}", UriKind.Relative),
        Method = HttpMethod.Post
      };

      Dictionary<string, string> postParameters = new();
      postParameters.Add("objects", JsonConvert.SerializeObject(hashes));
      childrenHttpMessage.Content = new FormUrlEncodedContent(postParameters);
      childrenHttpMessage.Headers.Add("Accept", "text/plain");

      HttpResponseMessage childrenHttpResponse = null;
      try
      {
        childrenHttpResponse = await Client
          .SendAsync(childrenHttpMessage, HttpCompletionOption.ResponseHeadersRead, CancellationToken)
          .ConfigureAwait(false);
        childrenHttpResponse.EnsureSuccessStatusCode();
      }
      catch (Exception e)
      {
        OnErrorAction?.Invoke(TransportName, e);
        return false;
      }

      childrenStream = await childrenHttpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
    }
    else
    {
      childrenStream = new MemoryStream();
    }

    using (var stream = childrenStream)
    using (var reader = new StreamReader(stream, Encoding.UTF8))
    {
      string line;
      while ((line = reader.ReadLine()) != null)
      {
        if (CancellationToken.IsCancellationRequested)
        {
          Queue = new ConcurrentQueue<(string, string, int)>();
          return false;
        }

        var pcs = line.Split(new[] { '\t' }, 2);
        targetTransport.SaveObject(pcs[0], pcs[1]);

        OnProgressAction?.Invoke(TransportName, 1); // possibly make this more friendly
      }
    }

    return true;
  }

  #endregion
}

/// <summary>
/// https://cymbeline.ch/2014/03/16/gzip-encoding-an-http-post-request-body/
/// </summary>
internal sealed class GzipContent : HttpContent
{
  private readonly HttpContent content;

  public GzipContent(HttpContent content)
  {
    if (content == null)
      return;

    this.content = content;

    // Keep the original content's headers ...
    if (content != null)
      foreach (KeyValuePair<string, IEnumerable<string>> header in content.Headers)
        Headers.TryAddWithoutValidation(header.Key, header.Value);

    // ... and let the server know we've Gzip-compressed the body of this request.
    Headers.ContentEncoding.Add("gzip");
  }

  protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
  {
    // Open a GZipStream that writes to the specified output stream.
    using (GZipStream gzip = new(stream, CompressionMode.Compress, true))
      // Copy all the input content to the GZip stream.
      if (content != null)
        await content.CopyToAsync(gzip).ConfigureAwait(false);
      else
        await new StringContent(string.Empty).CopyToAsync(gzip).ConfigureAwait(false);
  }

  protected override bool TryComputeLength(out long length)
  {
    length = -1;
    return false;
  }
}
