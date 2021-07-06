﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sentry;

namespace Speckle.Core.Logging
{
  /// <summary>
  ///  Anonymous telemetry to help us understand how to make a better Speckle.
  ///  This really helps us to deliver a better open source project and product!
  /// </summary>
  public static class Log
  {
    private static bool _initialized = false;

    public static void Initialize()
    {
      if (_initialized)
        return;

      var dsn = "https://f29ec716d14d4121bb2a71c4f3ef7786@o436188.ingest.sentry.io/5396846";

      var env = "production";
      var debug = false;
      #if DEBUG
            env = "dev";
            dsn = null;
            debug = true;
      #endif

      SentrySdk.Init(o =>
      {
        o.Dsn = dsn;
        o.Environment = env;
        o.Debug = debug;
        o.Release = "SpeckleCore@" + Assembly.GetExecutingAssembly().GetName().Version.ToString();
      });
      
      SentrySdk.ConfigureScope(scope =>
      {
        scope.User = new User { Id = Setup.SUUID, };
        scope.SetTag("hostApplication", Setup.HostApplication);
      });

      _initialized = true;
    }


    //capture and make sure Sentry is initialized
    public static void CaptureException(
      Exception e,
      SentryLevel level = SentryLevel.Info,
      List<KeyValuePair<string, object>> extra = null)
    {
      Initialize();

      SentrySdk.WithScope(s =>
      {
        s.Level = level;

        if (extra != null)
          s.SetExtras(extra);
        if (e is AggregateException aggregate)
          aggregate.InnerExceptions.ToList().ForEach(ex => SentrySdk.CaptureException(e));
        else
          SentrySdk.CaptureException(e);
      });
    }

    public static void AddBreadcrumb(string message)
    {
      Initialize();
      SentrySdk.AddBreadcrumb(message);
    }
  }
}
