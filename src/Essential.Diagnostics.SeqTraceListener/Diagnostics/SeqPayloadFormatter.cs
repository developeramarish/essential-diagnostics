﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Essential.Diagnostics
{
    static class SeqPayloadFormatter
    {
        static string EventTypeKey = "EventType";
        static string EventIdKey = "EventId";
        static string SourceKey = "Source";
        static string ActivityIdKey = "ActivityId";
        static string RelatedActivityIdKey = "RelatedActivityId";

        static readonly IDictionary<Type, Action<object, TextWriter>> LiteralWriters;

        static SeqPayloadFormatter()
        {
            LiteralWriters = new Dictionary<Type, Action<object, TextWriter>>
            {
                { typeof(bool), (v, w) => WriteBoolean((bool)v, w) },
                { typeof(char), (v, w) => WriteString(((char)v).ToString(CultureInfo.InvariantCulture), w) },
                { typeof(byte), WriteToString },
                { typeof(sbyte), WriteToString },
                { typeof(short), WriteToString },
                { typeof(ushort), WriteToString },
                { typeof(int), WriteToString },
                { typeof(uint), WriteToString },
                { typeof(long), WriteToString },
                { typeof(ulong), WriteToString },
                { typeof(float), WriteToString },
                { typeof(double), WriteToString },
                { typeof(decimal), WriteToString },
                { typeof(string), (v, w) => WriteString((string)v, w) },
                { typeof(DateTime), (v, w) => WriteDateTime((DateTime)v, w) },
                { typeof(DateTimeOffset), (v, w) => WriteDateTimeOffset((DateTimeOffset)v, w) },
            };
        }

        static readonly IDictionary<TraceEventType, string> LevelMap = new Dictionary<TraceEventType, string>
        {
            { TraceEventType.Verbose, "Verbose" },
            // { "Debug", "Debug" },
            { TraceEventType.Information, "Information" },
            { TraceEventType.Warning, "Warning" },
            { TraceEventType.Error, "Error" },
            { TraceEventType.Critical, "Fatal" },
            // This is somewhat important
            { TraceEventType.Transfer, "Information" },
            // Somewhat more detailed
            { TraceEventType.Start, "Verbose" },
            { TraceEventType.Stop, "Verbose" },
            { TraceEventType.Resume, "Verbose" },
            { TraceEventType.Suspend, "Verbose" },
        };

        public static bool IsLiteral(object value)
        {
            if (value == null)
            {
                return true;
            }
            var type = value.GetType();
            return LiteralWriters.ContainsKey(type);
        }

        public static void ToJson(IEnumerable<TraceData> events, TextWriter payload)
        {
            var currentOffset = DateTimeOffset.Now.Offset;

            var delim = "";
            foreach (var loggingEvent in events)
            {
                payload.Write(delim);
                delim = ",";
                ToJson(loggingEvent, currentOffset, payload);
            }
        }

        static string Escape(string s)
        {
            if (s == null) return null;

            StringBuilder escapedResult = null;
            var cleanSegmentStart = 0;
            for (var i = 0; i < s.Length; ++i)
            {
                var c = s[i];
                if (c < (char)32 || c == '\\' || c == '"')
                {

                    if (escapedResult == null)
                        escapedResult = new StringBuilder();

                    escapedResult.Append(s.Substring(cleanSegmentStart, i - cleanSegmentStart));
                    cleanSegmentStart = i + 1;

                    switch (c)
                    {
                        case '"':
                            {
                                escapedResult.Append("\\\"");
                                break;
                            }
                        case '\\':
                            {
                                escapedResult.Append("\\\\");
                                break;
                            }
                        case '\n':
                            {
                                escapedResult.Append("\\n");
                                break;
                            }
                        case '\r':
                            {
                                escapedResult.Append("\\r");
                                break;
                            }
                        case '\f':
                            {
                                escapedResult.Append("\\f");
                                break;
                            }
                        case '\t':
                            {
                                escapedResult.Append("\\t");
                                break;
                            }
                        default:
                            {
                                escapedResult.Append("\\u");
                                escapedResult.Append(((int)c).ToString("X4"));
                                break;
                            }
                    }
                }
            }

            if (escapedResult != null)
            {
                if (cleanSegmentStart != s.Length)
                    escapedResult.Append(s.Substring(cleanSegmentStart));

                return escapedResult.ToString();
            }

            return s;
        }

        static string SanitizeKey(string key)
        {
            //return new string(key.Replace(":", "_").Where(c => c == '_' || char.IsLetterOrDigit(c)).ToArray());

            var builder = new StringBuilder();
            foreach (var c in key.ToCharArray())
            {
                if (c == ':')
                {
                    builder.Append('_');
                }
                else if (c == '_' || char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }

        static void ToJson(TraceData traceData, TimeSpan currentOffset, TextWriter payload)
        {
            string level;
            if (!LevelMap.TryGetValue(traceData.EventType, out level))
            {
                level = "Verbose";
            }

            payload.Write("{");

            var parentCollection = new ArrayList();
            var delim = "";

            WriteJsonProperty("Timestamp", traceData.DateTime, ref delim, payload, parentCollection);
            WriteJsonProperty("Level", level, ref delim, payload, parentCollection);

            WriteJsonProperty("MessageTemplate", traceData.MessageFormat ?? string.Empty, ref delim, payload, parentCollection);

            // First (if any) Exception found in the message args
            if (traceData.Exception != null)
            {
                WriteJsonProperty("Exception", traceData.Exception, ref delim, payload, parentCollection);
            }

            payload.Write(",\"Properties\":{");

            var pdelim = "";
            var seenKeys = new List<string>();

            WriteJsonProperty(EventTypeKey, traceData.EventType, ref pdelim, payload, parentCollection);
            seenKeys.Add(EventTypeKey);

            if (traceData.Source != null)
            {
                WriteJsonProperty(SourceKey, traceData.Source, ref pdelim, payload, parentCollection);
                seenKeys.Add(SourceKey);
                WriteJsonProperty(EventIdKey, traceData.Id, ref pdelim, payload, parentCollection);
                seenKeys.Add(EventIdKey);
            }

            WriteJsonProperty(ActivityIdKey, traceData.ActivityId, ref pdelim, payload, parentCollection);
            seenKeys.Add(ActivityIdKey);

            if (traceData.RelatedActivityId.HasValue)
            {
                WriteJsonProperty(RelatedActivityIdKey, traceData.RelatedActivityId, ref pdelim, payload, parentCollection);
                seenKeys.Add(RelatedActivityIdKey);
            }

            if (traceData.Data != null && traceData.Data.Count > 0)
            {
                WriteJsonProperty("Data", traceData.Data, ref pdelim, payload, parentCollection);
                seenKeys.Add("Data");
            }

            if (traceData.MessageArgs != null)
            {
                for (var i = 0; i < traceData.MessageArgs.Count; ++i)
                {
                    var argKey = i.ToString(CultureInfo.InvariantCulture);
                    WriteJsonProperty(argKey, traceData.MessageArgs[i], ref pdelim, payload, parentCollection);
                    seenKeys.Add(argKey);
                }
            }

            if (traceData.Properties != null)
            {
                foreach (var property in traceData.Properties)
                {
                    var sanitizedKey = SanitizeKey(property.Key.ToString());
                    if (seenKeys.Contains(sanitizedKey))
                        continue;

                    seenKeys.Add(sanitizedKey);
                    WriteJsonProperty(sanitizedKey, property.Value, ref pdelim, payload, parentCollection);
                }
            }

            payload.Write("}");
            payload.Write("}");
        }

        static void WriteArray(IList array, TextWriter output, ArrayList parentCollections)
        {
            var newParentCollections = new ArrayList(parentCollections);
            newParentCollections.Add(array);
            output.Write("[");
            for (var index = 0; index < array.Count; index++)
            {
                if (index > 0)
                {
                    output.Write(",");
                }
                var value = array[index];
                WritePropertyValue(value, output, newParentCollections);
            }
            output.Write("]");
        }

        static void WriteBoolean(bool value, TextWriter output)
        {
            output.Write(value ? "true" : "false");
        }

        static void WriteDateTime(DateTime value, TextWriter output)
        {
            output.Write("\"");
            output.Write(value.ToString("o"));
            output.Write("\"");
        }

        static void WriteDateTimeOffset(DateTimeOffset value, TextWriter output)
        {
            output.Write("\"");
            output.Write(value.ToString("o"));
            output.Write("\"");
        }

        static void WriteDictionary(IDictionary<string, object> dictionary, TextWriter output, ArrayList parentCollections)
        {
            var newParentCollections = new ArrayList(parentCollections);
            newParentCollections.Add(dictionary);
            output.Write("{");
            var delimiter = "";
            foreach (var kvp in dictionary)
            {
                WriteJsonProperty(kvp.Key, kvp.Value, ref delimiter, output, newParentCollections);
            }
            output.Write("}");
        }

        static void WriteJsonProperty(string name, object value, ref string precedingDelimiter, TextWriter output, ArrayList parentCollections)
        {
            output.Write(precedingDelimiter);
            WritePropertyName(name, output);
            WritePropertyValue(value, output, parentCollections);
            precedingDelimiter = ",";
        }

        static void WritePropertyName(string name, TextWriter output)
        {
            output.Write("\"");
            output.Write(name);
            output.Write("\":");
        }

        static void WritePropertyValue(object value, TextWriter output, ArrayList parentCollections)
        {
            if (value == null)
            {
                output.Write("null");
                return;
            }
            if (value is IDictionary<string, object> && !parentCollections.Contains(value))
            {
                WriteDictionary((IDictionary<string, object>)value, output, parentCollections);
                return;
            }
            if (value is IList && !parentCollections.Contains(value))
            {
                WriteArray((IList)value, output, parentCollections);
                return;
            }
            Action<object, TextWriter> writer;
            if (LiteralWriters.TryGetValue(value.GetType(), out writer))
            {
                writer(value, output);
                return;
            }
            WriteString(value.ToString(), output);
        }

        static void WriteString(string value, TextWriter output)
        {
            var content = Escape(value);
            output.Write("\"");
            output.Write(content);
            output.Write("\"");
        }

        static void WriteToString(object number, TextWriter output)
        {
            output.Write(number.ToString());
        }

        private delegate void Action<T1, T2>(T1 a, T2 b);
    }
}
