using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace FF14BackupTool.Core
{
    public static class JsonHelper
    {
        public static string Serialize<T>(T value)
        {
            var settings = new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            };
            var serializer = new DataContractJsonSerializer(typeof(T), settings);
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidDataException("JSON 内容为空。");

            var settings = new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            };
            var serializer = new DataContractJsonSerializer(typeof(T), settings);
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                object value = serializer.ReadObject(stream);
                return (T)value;
            }
        }

        public static T Deserialize<T>(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new InvalidDataException("JSON 内容为空。");
            var settings = new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            };
            var serializer = new DataContractJsonSerializer(typeof(T), settings);
            using (var stream = new MemoryStream(bytes))
            {
                object value = serializer.ReadObject(stream);
                return (T)value;
            }
        }
    }
}
