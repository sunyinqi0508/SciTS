using Npgsql;
using Serilog;
using System;
using System.Threading.Tasks;
using BenchmarkTool.Queries;
using BenchmarkTool.Generators;
using System.Diagnostics;
using System.IO;
using BenchmarkTool.Database.Queries;
using System.Numerics;

namespace BenchmarkTool.Database
{
    public class AQueryDB : IDatabase
    {
        private IQuery _query;
        private int _aggInterval;
        private Process aq_handle;
        private StreamWriter aq_stdin;
        private StreamReader aq_stdout;
        private String base62uuid(int crop = 8) {
            String base62alp = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
            byte[] uuid = Guid.NewGuid().ToByteArray();
            var t = new BigInteger(uuid, true);
            String ret = "";
            while(t != 0){
                ret = base62alp[(int)(t%62)] + ret;
                t/=62;
            }
            return ret.Substring(0, crop);
        }

        protected AQueryDB(IQuery query)
        {
            _query = query;
            _aggInterval = Config.GetAggregationInterval();
        }

        public AQueryDB() : this(new AQueryQuery())
        {
        }

        public void Cleanup()
        {
        }

        public void Close()
        {
            try
            {
                aq_stdin.Close();
                aq_handle.Kill();
            }
            catch (Exception ex)
            {
                Log.Error(String.Format("Failed to close. Exception: {0}", ex.ToString()));
            }
        }

        public void Init()
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.WorkingDirectory = BenchmarkTool.Config.GetAQueryPath();
            psi.FileName = BenchmarkTool.Config.GetAQueryCMD();
            psi.Arguments = BenchmarkTool.Config.GetAQueryArgs();
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            aq_handle = new Process();
            aq_handle.StartInfo = psi;
            aq_handle.Start();
            aq_stdin = aq_handle.StandardInput;
            aq_stdout = aq_handle.StandardOutput;
        }

        private void AQExecuteQuery(NpgsqlCommand cmd)
        {
            AQExecuteQuery(cmd.CommandText);                
        }
        private void AQExecuteQuery(String cmd){
            aq_stdin.WriteLine(cmd);                
            aq_stdin.WriteLine("exec");
            String token = base62uuid();
            aq_stdin.WriteLine("echo " + token);
            String line = aq_stdout.ReadLine();
            while (line != null && !line.Contains(token))  {
                line = aq_stdout.ReadLine();
            }
        }
        public QueryStatusRead OutOfRangeQuery(OORangeQuery query)
        {
            try
            {
                using var cmd = new NpgsqlCommand(_query.OutOfRange);
                cmd.Parameters.AddWithValue(QueryParams.Start, NpgsqlTypes.NpgsqlDbType.Timestamp, query.StartDate);
                cmd.Parameters.AddWithValue(QueryParams.End, NpgsqlTypes.NpgsqlDbType.Timestamp, query.EndDate);
                cmd.Parameters.AddWithValue(QueryParams.SensorID, NpgsqlTypes.NpgsqlDbType.Integer, query.SensorID);
                cmd.Parameters.AddWithValue(QueryParams.MaxVal, NpgsqlTypes.NpgsqlDbType.Double, query.MaxValue);
                cmd.Parameters.AddWithValue(QueryParams.MinVal, NpgsqlTypes.NpgsqlDbType.Double, query.MinValue);
                var points = 0;
                Stopwatch sw = Stopwatch.StartNew();
                AQExecuteQuery(cmd);
                sw.Stop();

                return new QueryStatusRead(true, points, new PerformanceMetricRead(sw.ElapsedMilliseconds, points, 0, query.StartDate, query.DurationMinutes, 0, Operation.OutOfRangeQuery));
            }
            catch (Exception ex)
            {
                Log.Error(String.Format("Failed to execute Out of Range Query. Exception: {0}", ex.ToString()));
                return new QueryStatusRead(false, 0, new PerformanceMetricRead(0, 0, 0, query.StartDate, query.DurationMinutes, 0, Operation.OutOfRangeQuery), ex, ex.ToString());
            }
        }


        public QueryStatusRead AggregatedDifferenceQuery(ComparisonQuery query)
        {
            try
            {
                using var cmd = new NpgsqlCommand(_query.AggDifference);
                cmd.Parameters.AddWithValue(QueryParams.Start, NpgsqlTypes.NpgsqlDbType.Timestamp, query.StartDate);
                cmd.Parameters.AddWithValue(QueryParams.End, NpgsqlTypes.NpgsqlDbType.Timestamp, query.EndDate);
                cmd.Parameters.AddWithValue(QueryParams.FirstSensorID, NpgsqlTypes.NpgsqlDbType.Integer, query.FirstSensorID);
                cmd.Parameters.AddWithValue(QueryParams.SecondSensorID, NpgsqlTypes.NpgsqlDbType.Integer, query.SecondSensorID);

                Stopwatch sw = Stopwatch.StartNew();
                var points = 0;
                AQExecuteQuery(cmd);
                sw.Stop();

                return new QueryStatusRead(true, points, new PerformanceMetricRead(sw.ElapsedMilliseconds, points, 0, query.StartDate, query.DurationMinutes, 0, Operation.DifferenceAggQuery));
            }
            catch (Exception ex)
            {
                Log.Error(String.Format("Failed to execute Difference beween agg sensor values query. Exception: {0}", ex.ToString()));
                return new QueryStatusRead(false, 0, new PerformanceMetricRead(0, 0, 0, query.StartDate, query.DurationMinutes, 0, Operation.DifferenceAggQuery), ex, ex.ToString());
            }
        }


        public QueryStatusRead RangeQueryAgg(RangeQuery query)
        {
            try
            {
                Log.Information("Start date: {0}, end date: {1}, sensors: {2}", query.StartDate, query.EndDate, query.SensorFilter);
                using var cmd = new NpgsqlCommand(_query.RangeAgg);
                cmd.Parameters.AddWithValue(QueryParams.Start, NpgsqlTypes.NpgsqlDbType.Timestamp, query.StartDate);
                cmd.Parameters.AddWithValue(QueryParams.End, NpgsqlTypes.NpgsqlDbType.Timestamp, query.EndDate);
                cmd.Parameters.AddWithValue(QueryParams.SensorIDs, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, query.SensorIDs);

                var points = 0;
                Stopwatch sw = Stopwatch.StartNew();
                AQExecuteQuery(cmd);
                sw.Stop();

                return new QueryStatusRead(true, points, new PerformanceMetricRead(sw.ElapsedMilliseconds, points, 0, query.StartDate, query.DurationMinutes, _aggInterval, Operation.RangeQueryAggData));
            }
            catch (Exception ex)
            {
                Log.Error(String.Format("Failed to execute Range Query Aggregated Data. Exception: {0}", ex.ToString()));
                return new QueryStatusRead(false, 0, new PerformanceMetricRead(0, 0, 0, query.StartDate, query.DurationMinutes, _aggInterval, Operation.RangeQueryAggData), ex, ex.ToString());
            }
        }

        public QueryStatusRead RangeQueryRaw(RangeQuery query)
        {
            try
            {
                Log.Information(String.Format("Start Date: {0}", query.StartDate.ToString()));
                Log.Information(String.Format("End Date: {0}", query.EndDate.ToString()));

                using var cmd = new NpgsqlCommand(_query.RangeRaw);
                cmd.Parameters.AddWithValue(QueryParams.Start, NpgsqlTypes.NpgsqlDbType.Timestamp, query.StartDate);
                cmd.Parameters.AddWithValue(QueryParams.End, NpgsqlTypes.NpgsqlDbType.Timestamp, query.EndDate);
                cmd.Parameters.AddWithValue(QueryParams.SensorIDs, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, query.SensorIDs);
                var points = 0;
                Stopwatch sw = Stopwatch.StartNew();
                AQExecuteQuery(cmd);
                sw.Stop();

                return new QueryStatusRead(true, points, new PerformanceMetricRead(sw.ElapsedMilliseconds, points, 0, query.StartDate, query.DurationMinutes, _aggInterval, Operation.RangeQueryRawData));
            }
            catch (Exception ex)
            {
                Log.Error(String.Format("Failed to execute Range Query Raw Data. Exception: {0}", ex.ToString()));
                return new QueryStatusRead(false, 0, new PerformanceMetricRead(0, 0, 0, query.StartDate, query.DurationMinutes, _aggInterval, Operation.RangeQueryRawData), ex, ex.ToString());
            }
        }

        public QueryStatusRead StandardDevQuery(SpecificQuery query)
        {
            try
            {
                using var cmd = new NpgsqlCommand(_query.StdDev);
                cmd.Parameters.AddWithValue(QueryParams.Start, NpgsqlTypes.NpgsqlDbType.Timestamp, query.StartDate);
                cmd.Parameters.AddWithValue(QueryParams.End, NpgsqlTypes.NpgsqlDbType.Timestamp, query.EndDate);
                cmd.Parameters.AddWithValue(QueryParams.SensorID, NpgsqlTypes.NpgsqlDbType.Integer, query.SensorID);

                Stopwatch sw = Stopwatch.StartNew();
                var points = 0;
                AQExecuteQuery(cmd);
                sw.Stop();

                return new QueryStatusRead(true, points, new PerformanceMetricRead(sw.ElapsedMilliseconds, points, 0, query.StartDate, query.DurationMinutes, 0, Operation.STDDevQuery));
            }
            catch (Exception ex)
            {
                Log.Error(String.Format("Failed to execute STD Dev query. Exception: {0}", ex.ToString()));
                return new QueryStatusRead(false, 0, new PerformanceMetricRead(0, 0, 0, query.StartDate, query.DurationMinutes, 0, Operation.STDDevQuery), ex, ex.ToString());
            }
        }

        public async Task<QueryStatusWrite> WriteRecord(IRecord record)
        {
            try
            {
                var stmt = $"INSERT INTO {Constants.TableName} ({Constants.SensorID}, {Constants.Value}, {Constants.Time}) VALUES ({record.SensorID}, {record.Value}, {record.Time})";
                Stopwatch sw = Stopwatch.StartNew();
                AQExecuteQuery(stmt);
                sw.Stop();
                return new QueryStatusWrite(true, new PerformanceMetricWrite(sw.ElapsedMilliseconds, 1, 0, Operation.StreamIngestion));
            }
            catch (Exception ex)
            {
                Log.Error(String.Format("Failed to insert batch. Exception: {0}", ex.ToString()));
                return new QueryStatusWrite(false, 0, new PerformanceMetricWrite(0, 0, 1, Operation.StreamIngestion), ex, ex.ToString());
            }
        }

        public async Task<QueryStatusWrite> WriteBatch(Batch batch)
        {
            throw new NotImplementedException();
        }
    }
}
