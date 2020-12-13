﻿extern alias DynamoNewtonsoft;
using DNJ = DynamoNewtonsoft::Newtonsoft.Json;
using Dynamo.Graph.Nodes;
using ProtoCore.AST.AssociativeAST;
using Speckle.ConnectorDynamo.Functions;
using Speckle.Core.Credentials;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Speckle.Core.Logging;
using Dynamo.Engine;
using ProtoCore.Mirror;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using Dynamo.Utilities;
using Speckle.Core.Api;
using Speckle.Core.Models;

namespace Speckle.ConnectorDynamo.SendNode
{
  /// <summary>
  /// Send data to Speckle
  /// </summary>
  [NodeName("Send")]
  [NodeCategory("Speckle 2")]
  [NodeDescription("Send data to a Speckle server")]
  [NodeSearchTags("send", "speckle")]
  [IsDesignScriptCompatible]
  public class Send : NodeModel
  {
    #region fields & props

    #region private fields

    private bool _transmitting = false;
    private string _message = "";
    private double _progress = 0;
    private string _expiredCount = "";
    private bool _sendEnabled = false;
    private int _objectCount = 0;
    private bool _hasOutput = false;
    private string _outputInfo = "";
    private bool _firstRun = true;
    private CancellationTokenSource _cancellationToken;

    //cached inputs
    private object _data { get; set; }
    private List<StreamWrapper> _streams { get; set; }
    private List<string> _branchNames { get; set; }
    private string _commitMessage { get; set; }

    #endregion

    #region ui bindings

    //PUBLIC PROPERTIES
    //NOT to be saved with the file

    /// <summary>
    /// UI Binding
    /// </summary>
    [DNJ.JsonIgnore]
    public bool Transmitting
    {
      get => _transmitting;
      set
      {
        _transmitting = value;
        RaisePropertyChanged("Transmitting");
      }
    }

    /// <summary>
    /// UI Binding
    /// </summary>
    [DNJ.JsonIgnore]
    public string Message
    {
      get => _message;
      set
      {
        _message = value;
        RaisePropertyChanged("Message");
      }
    }

    /// <summary>
    /// UI Binding
    /// </summary>
    [DNJ.JsonIgnore]
    public double Progress
    {
      get => _progress;
      set
      {
        _progress = value;
        RaisePropertyChanged("Progress");
      }
    }

    /// <summary>
    /// UI Binding
    /// </summary>
    [DNJ.JsonIgnore]
    public bool SendEnabled
    {
      get => _sendEnabled;
      set
      {
        _sendEnabled = value;
        RaisePropertyChanged("SendEnabled");
      }
    }

    //properties TO SAVE
    /// <summary>
    /// UI Binding
    /// </summary>
    public string ExpiredCount
    {
      get => _expiredCount;
      set
      {
        _expiredCount = value;
        RaisePropertyChanged("ExpiredCount");
      }
    }

    #endregion

    #endregion

    /// <summary>
    /// JSON constructor, called on file open
    /// </summary>
    /// <param name="inPorts"></param>
    /// <param name="outPorts"></param>
    [DNJ.JsonConstructor]
    private Send(IEnumerable<PortModel> inPorts, IEnumerable<PortModel> outPorts) : base(inPorts, outPorts)
    {
      if (inPorts.Count() == 4)
      {
        //blocker: https://github.com/DynamoDS/Dynamo/issues/11118
        //inPorts.ElementAt(1).DefaultValue = endPortDefaultValue;
      }
      else
      {
        // If information from json does not look correct, clear the default ports and add ones with default value
        InPorts.Clear();
        AddInputs();
      }

      if (outPorts.Count() == 0)
        AddOutputs();

      ArgumentLacing = LacingStrategy.Disabled;
      this.PropertyChanged += HandlePropertyChanged;
    }

    /// <summary>
    /// Normal constructor, called when adding node to canvas
    /// </summary>
    public Send()
    {
      Tracker.TrackPageview(Tracker.SEND_ADDED);

      AddInputs();
      AddOutputs();

      RegisterAllPorts();
      ArgumentLacing = LacingStrategy.Disabled;
      this.PropertyChanged += HandlePropertyChanged;
    }

    private void AddInputs()
    {
      StringNode defaultBranchValue = new StringNode();
      StringNode defaultMessageValue = new StringNode();

      defaultBranchValue.Value = "main";
      defaultMessageValue.Value = "Automatic commit from Dynamo";

      InPorts.Add(new PortModel(PortType.Input, this, new PortData("data", "The data to send")));
      InPorts.Add(new PortModel(PortType.Input, this, new PortData("stream", "The stream or streams to send to")));
      InPorts.Add(new PortModel(PortType.Input, this,
        new PortData("branchName", "The branch you want your commit associated with.", defaultBranchValue)));
      InPorts.Add(new PortModel(PortType.Input, this,
        new PortData("message", "Commit message. If left blank, one will be generated for you.", defaultMessageValue)));
    }

    private void AddOutputs()
    {
      OutPorts.Add(new PortModel(PortType.Output, this, new PortData("stream", "Stream or streams pointing to the created commit")));
    }


    internal void CancelSend()
    {
      _cancellationToken.Cancel();
      ResetNode();
    }

    /// <summary>
    /// Takes care of actually sending the data
    /// </summary>
    /// <param name="engine"></param>
    internal void DoSend(EngineController engine)
    {
      //double check, but can probably remove it
      if (!InPorts[0].IsConnected || !InPorts[1].IsConnected)
      {
        ResetNode(true);
        return;
      }

      //if already receiving, stop and start again
      if (Transmitting)
        CancelSend();

      Tracker.TrackPageview(Tracker.SEND_MANUAL);

      Transmitting = true;
      Message = "Converting...";
      _cancellationToken = new CancellationTokenSource();

      try
      {
        if (_streams == null)
          Core.Logging.Log.CaptureAndThrow(new Exception("The stream provided is invalid"));
        if (_data == null)
          Core.Logging.Log.CaptureAndThrow(new Exception("The data provided is invalid"));


        var converter = new BatchConverter();
        var @base = converter.ConvertRecursivelyToSpeckle(_data);
        var totalCount = @base.GetTotalChildrenCount();
        Message = "Sending...";

        void ProgressAction(ConcurrentDictionary<string, int> dict)
        {
          var val = (double)dict.Values.Average() / totalCount;
          Message = val.ToString("0%");
          Progress = val * 100;
        }

        var hasErrors = false;

        void ErrorAction(string transportName, Exception e)
        {
          hasErrors = true;
          Message = e.InnerException != null ? e.InnerException.Message : e.Message;
          Message = Message.Contains("401") ? "Not authorized" : Message;
          _cancellationToken.Cancel();
        }

        var plural = (totalCount == 1) ? "" : "s";
        _commitMessage = string.IsNullOrEmpty(_commitMessage)
          ? $"Sent {totalCount} object{plural} from Dynamo"
          : _commitMessage;

        var commitIds = Functions.Functions.Send(@base, _streams, _cancellationToken.Token, _branchNames,
          _commitMessage,
          ProgressAction, ErrorAction);

        if (!hasErrors && commitIds != null)
        {
          for (int i = 0; i < _streams.Count; i++)
          {
            _streams[i].CommitId = commitIds[i];
          }

          _outputInfo = string.Join("|", _streams.Select(x => x.ToString()));
          Message = "";
        }
      }
      catch (Exception e)
      {
        _cancellationToken.Cancel();
        Message = e.InnerException != null ? e.InnerException.Message : e.Message;
        //temp exclusion of core bug
        if (!(e.InnerException != null && e.InnerException.Message ==
          "Cannot resolve reference. The provided transport could not find it."))
          Core.Logging.Log.CaptureAndThrow(e);
      }
      finally
      {
        ResetNode();
        if (!_cancellationToken.IsCancellationRequested)
        {
          _hasOutput = true;
          ExpireNode();
        }
      }
    }

    /// <summary>
    /// Reset the node UI
    /// </summary>
    /// <param name="hardReset">If true, resets enabled status too</param>
    private void ResetNode(bool hardReset = false)
    {
      Transmitting = false;
      ExpiredCount = "";
      Progress = 0;
      _hasOutput = false;
      if (hardReset)
      {
        _streams = null;
        _branchNames = null;
        _data = null;
        _objectCount = 0;
        SendEnabled = false;
        Message = "";
      }
    }

    /// <summary>
    /// Triggered when the node inputs change
    /// Caches a copy of the inputs
    /// </summary>
    /// <param name="engine"></param>
    internal void LoadInputs(EngineController engine)
    {
      ResetNode(true);

      try
      {
        _data = GetInputAs<object>(engine, 0, true);
      }
      catch
      {
        ResetNode(true);
        Message = "Data input is invalid";
        return;
      }

      //this port accepts:
      //a stream wrapper, a url, a list of stream wrappers or a list of urls
      try
      {
        _streams = new List<StreamWrapper>();
        var inputStream = GetInputAs<object>(engine, 1);
        switch (inputStream)
        {
          case StreamWrapper s:
            _streams.Add(new StreamWrapper(s.StreamId, s.AccountId, s.ServerUrl));
            break;
          case string s:
            _streams.Add(new StreamWrapper(s));
            break;
          case List<object> s:
            try
            {
              var ss = s.Cast<StreamWrapper>();
              _streams.AddRange(ss.Select(x => new StreamWrapper(x.StreamId, x.AccountId, x.ServerUrl)));
              break;
            }
            catch
            {
              //ignored
            }

            try
            {
              var ss = s.Cast<string>();
              _streams.AddRange(ss.Select(x => new StreamWrapper(x)));
              break;
            }
            catch
            {
              //ignored
            }

            break;
        }
      }
      catch
      {
        //ignored
      }

      if (_streams == null || !_streams.Any())
      {
        ResetNode(true);
        Message = "Stream is invalid";
        return;
      }

      //get branch name/s
      if (InPorts[2].Connectors.Any())
      {
        try
        {
          _branchNames = new List<string>();
          var branch = GetInputAs<object>(engine, 2);
          switch (branch)
          {
            case string b:
              _branchNames.Add(b);
              break;
            case List<string> b:
              _branchNames.AddRange(b);
              break;
          }

          if (_streams.Count != _branchNames.Count)
          {
            Message = "Branch count is invalid, will use `main`";
            _branchNames = null;
          }
        }
        catch
        {
          Message = "Branch name is invalid, will use `main`";
          _branchNames = null;
        }
      }

      try
      {
        _commitMessage =
          InPorts[3].Connectors.Any()
            ? GetInputAs<string>(engine, 3)
            : ""; //IsConnected not working because has default value
      }
      catch
      {
        Message = "Message is invalid, will skip it";
      }


      InitializeSender();
    }

    private void InitializeSender()
    {
      SendEnabled = true;

      if (_data is Base @base)
      {
        //_objectCount is updated when the RecurseInput function loops through the data, not ideal but it works
        //if we're dealing with a single Base (preconverted obj) use GetTotalChildrenCount to count its children
        _objectCount = (int)@base.GetTotalChildrenCount();
        //exclude wrapper obj.... this is a bit of a hack...
        if (_objectCount > 1) _objectCount--;
      }

      ExpiredCount = _objectCount.ToString();
      if (string.IsNullOrEmpty(Message))
        Message = "Updates ready";
    }


    private T GetInputAs<T>(EngineController engine, int port, bool count = false)
    {
      var valuesNode = InPorts[port].Connectors[0].Start.Owner;
      var valuesIndex = InPorts[port].Connectors[0].Start.Index;
      var astId = valuesNode.GetAstIdentifierForOutputIndex(valuesIndex).Name;
      var inputMirror = engine.GetMirror(astId);


      if (inputMirror == null || inputMirror.GetData() == null) return default(T);

      var data = inputMirror.GetData();
      var value = RecurseInput(data, count);

      return (T)value;
    }

    private object RecurseInput(MirrorData data, bool count)
    {
      object @object;
      if (data.IsCollection)
      {
        var list = new List<object>();
        var elements = data.GetElements();
        list.AddRange(elements.Select(x => RecurseInput(x, count)));
        @object = list;
      }
      else
      {
        @object = data.Data;
        if (count)
          _objectCount++;
      }

      return @object;
    }


    private void ExpireNode()
    {
      OnNodeModified(true);
    }

    #region events

    internal event Action OnInputsChanged;

    /// <summary>
    /// Node inputs have changed, trigger load of new inputs
    /// </summary>
    protected virtual void RequestNewInputs()
    {
      OnInputsChanged?.Invoke();
    }

    void HandlePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
      if (e.PropertyName != "CachedValue")
        return;

      if (!InPorts[0].IsConnected || !InPorts[1].IsConnected)
      {
        ResetNode(true);
        return;
      }

      //prevent running when opening a saved file
      // if (_firstRun)
      // {
      //   _firstRun = false;
      //   return;
      // }

      //the node was expired manually just to output the commit info, no need to do anything!
      if (_hasOutput)
      {
        _hasOutput = false;
        return;
      }

      RequestNewInputs();
    }

    #endregion

    #region overrides

    /// <summary>
    /// Sending is actually only happening when clicking the button on the node UI, this function is only used to check what ports are connected
    /// could be done differently, but this is an easy way
    /// </summary>
    /// <param name="inputAstNodes"></param>
    /// <returns></returns>
    public override IEnumerable<AssociativeNode> BuildOutputAst(List<AssociativeNode> inputAstNodes)
    {
      if (!InPorts[0].IsConnected || !InPorts[1].IsConnected || !_hasOutput)
      {
        return OutPorts.Enumerate().Select(output =>
          AstFactory.BuildAssignment(GetAstIdentifierForOutputIndex(output.Index), new NullNode()));
      }

      var dataFunctionCall = AstFactory.BuildFunctionCall(
        new Func<string, object>(Functions.Functions.SendData),
        new List<AssociativeNode> { AstFactory.BuildStringNode(_outputInfo) });

      return new[] { AstFactory.BuildAssignment(GetAstIdentifierForOutputIndex(0), dataFunctionCall) };
    }

    #endregion
  }
}