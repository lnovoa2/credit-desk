using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Web;
using Mono.Data.Sqlite;

public static class LegacyRepository
{
    private static string DbPath
    {
        get
        {
            string root = HttpRuntime.AppDomainAppPath;
            return Path.Combine(root, "App_Data", "legacy-credit-desk.db");
        }
    }

    private static string ConnectionString
    {
        get { return "URI=file:" + DbPath + ";Version=3"; }
    }

    public static void EnsureDatabase()
    {
        lock (LegacyState.SyncRoot)
        {
            string dir = Path.GetDirectoryName(DbPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            bool firstRun = !File.Exists(DbPath);

            using (SqliteConnection cn = OpenConnection())
            using (SqliteCommand cmd = cn.CreateCommand())
            {
                cmd.CommandText = SchemaSql;
                cmd.ExecuteNonQuery();
            }

            if (firstRun || ScalarInt("SELECT COUNT(*) FROM Customers") == 0)
                SeedDatabase();
        }
    }

    public static DataSet Load()
    {
        lock (LegacyState.SyncRoot)
        {
            EnsureDatabase();
            DataSet ds = BuildSchema();
            using (SqliteConnection cn = OpenConnection())
            {
                FillTable(cn, ds.Tables["Customers"], "SELECT Id,Name,Country,Since,CreditLimit,OutstandingDebt,OverdueDays,RiskScore,Blocked,ManualOverride FROM Customers ORDER BY Id");
                FillTable(cn, ds.Tables["Orders"], "SELECT Id,CustomerId,Amount,PaymentMethod,Priority,Status,DecisionCode,DecisionText,CreatedAt FROM Orders ORDER BY CreatedAt");
                FillTable(cn, ds.Tables["Audit"], "SELECT Id,At,Type,Subject,Detail FROM Audit ORDER BY At");
            }
            ds.AcceptChanges();
            return ds;
        }
    }

    public static void Save(DataSet ds)
    {
        lock (LegacyState.SyncRoot)
        {
            EnsureDatabase();
            using (SqliteConnection cn = OpenConnection())
            using (SqliteTransaction tx = cn.BeginTransaction())
            {
                SaveCustomers(cn, tx, ds.Tables["Customers"]);
                SaveOrders(cn, tx, ds.Tables["Orders"]);
                SaveAudit(cn, tx, ds.Tables["Audit"]);
                tx.Commit();
                ds.AcceptChanges();
            }
        }
    }

    public static DataRow FindCustomer(DataSet ds, string id)
    {
        DataRow[] rows = ds.Tables["Customers"].Select("Id='" + Escape(id) + "'");
        return rows.Length == 0 ? null : rows[0];
    }

    // Deliberately reaches back into the database although a DataSet is already in memory.
    // This mixed access pattern is typical legacy debt and is useful for migration tests.
    public static decimal SumOpenOrders(DataSet ds, string customerId)
    {
        EnsureDatabase();
        using (SqliteConnection cn = OpenConnection())
        using (SqliteCommand cmd = cn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(Amount),0) FROM Orders WHERE CustomerId=@id AND Status<>'BLOCKED'";
            cmd.Parameters.AddWithValue("@id", customerId);
            return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public static int CountRecentOrders(DataSet ds, string customerId, int days)
    {
        EnsureDatabase();
        DateTime edge = DateTime.Now.AddDays(-days);
        using (SqliteConnection cn = OpenConnection())
        using (SqliteCommand cmd = cn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Orders WHERE CustomerId=@id AND CreatedAt>=@edge";
            cmd.Parameters.AddWithValue("@id", customerId);
            cmd.Parameters.AddWithValue("@edge", ToDbDate(edge));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public static void AddAudit(DataSet ds, string type, string subject, string detail)
    {
        DataRow row = ds.Tables["Audit"].NewRow();
        row["Id"] = Guid.NewGuid().ToString("N");
        row["At"] = DateTime.Now;
        row["Type"] = type;
        row["Subject"] = subject;
        row["Detail"] = detail;
        ds.Tables["Audit"].Rows.Add(row);
    }

    private static SqliteConnection OpenConnection()
    {
        SqliteConnection cn = new SqliteConnection(ConnectionString);
        cn.Open();
        using (SqliteCommand cmd = cn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }
        return cn;
    }

    private static void FillTable(SqliteConnection cn, DataTable table, string sql)
    {
        using (SqliteCommand cmd = cn.CreateCommand())
        {
            cmd.CommandText = sql;
            using (SqliteDataReader rd = cmd.ExecuteReader())
            {
                while (rd.Read())
                {
                    DataRow r = table.NewRow();
                    int i;
                    for (i = 0; i < table.Columns.Count; i++)
                    {
                        string name = table.Columns[i].ColumnName;
                        object v = rd[name];
                        if (v == DBNull.Value) { r[name] = DBNull.Value; continue; }
                        Type t = table.Columns[i].DataType;
                        if (t == typeof(DateTime)) r[name] = DateTime.Parse(Convert.ToString(v), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                        else if (t == typeof(bool)) r[name] = Convert.ToInt32(v) != 0;
                        else if (t == typeof(decimal)) r[name] = Convert.ToDecimal(v, CultureInfo.InvariantCulture);
                        else r[name] = Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
                    }
                    table.Rows.Add(r);
                }
            }
        }
    }

    private static void SaveCustomers(SqliteConnection cn, SqliteTransaction tx, DataTable table)
    {
        int i;
        for (i = 0; i < table.Rows.Count; i++)
        {
            DataRow r = table.Rows[i];
            if (r.RowState == DataRowState.Unchanged) continue;
            using (SqliteCommand cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                if (r.RowState == DataRowState.Deleted)
                {
                    cmd.CommandText = "DELETE FROM Customers WHERE Id=@Id";
                    cmd.Parameters.AddWithValue("@Id", r["Id", DataRowVersion.Original]);
                }
                else if (r.RowState == DataRowState.Added)
                {
                    cmd.CommandText = "INSERT INTO Customers(Id,Name,Country,Since,CreditLimit,OutstandingDebt,OverdueDays,RiskScore,Blocked,ManualOverride) VALUES(@Id,@Name,@Country,@Since,@CreditLimit,@OutstandingDebt,@OverdueDays,@RiskScore,@Blocked,@ManualOverride)";
                    AddCustomerParameters(cmd, r);
                }
                else
                {
                    cmd.CommandText = "UPDATE Customers SET Name=@Name,Country=@Country,Since=@Since,CreditLimit=@CreditLimit,OutstandingDebt=@OutstandingDebt,OverdueDays=@OverdueDays,RiskScore=@RiskScore,Blocked=@Blocked,ManualOverride=@ManualOverride WHERE Id=@Id";
                    AddCustomerParameters(cmd, r);
                }
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static void AddCustomerParameters(SqliteCommand cmd, DataRow r)
    {
        cmd.Parameters.AddWithValue("@Id", r["Id"]);
        cmd.Parameters.AddWithValue("@Name", r["Name"]);
        cmd.Parameters.AddWithValue("@Country", r["Country"]);
        cmd.Parameters.AddWithValue("@Since", ToDbDate(Convert.ToDateTime(r["Since"])));
        cmd.Parameters.AddWithValue("@CreditLimit", r["CreditLimit"]);
        cmd.Parameters.AddWithValue("@OutstandingDebt", r["OutstandingDebt"]);
        cmd.Parameters.AddWithValue("@OverdueDays", r["OverdueDays"]);
        cmd.Parameters.AddWithValue("@RiskScore", r["RiskScore"]);
        cmd.Parameters.AddWithValue("@Blocked", Convert.ToBoolean(r["Blocked"]) ? 1 : 0);
        cmd.Parameters.AddWithValue("@ManualOverride", Convert.ToBoolean(r["ManualOverride"]) ? 1 : 0);
    }

    private static void SaveOrders(SqliteConnection cn, SqliteTransaction tx, DataTable table)
    {
        int i;
        for (i = 0; i < table.Rows.Count; i++)
        {
            DataRow r = table.Rows[i];
            if (r.RowState == DataRowState.Unchanged) continue;
            using (SqliteCommand cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                if (r.RowState == DataRowState.Deleted)
                {
                    cmd.CommandText = "DELETE FROM Orders WHERE Id=@Id";
                    cmd.Parameters.AddWithValue("@Id", r["Id", DataRowVersion.Original]);
                }
                else if (r.RowState == DataRowState.Added)
                {
                    cmd.CommandText = "INSERT INTO Orders(Id,CustomerId,Amount,PaymentMethod,Priority,Status,DecisionCode,DecisionText,CreatedAt) VALUES(@Id,@CustomerId,@Amount,@PaymentMethod,@Priority,@Status,@DecisionCode,@DecisionText,@CreatedAt)";
                    AddOrderParameters(cmd, r);
                }
                else
                {
                    cmd.CommandText = "UPDATE Orders SET CustomerId=@CustomerId,Amount=@Amount,PaymentMethod=@PaymentMethod,Priority=@Priority,Status=@Status,DecisionCode=@DecisionCode,DecisionText=@DecisionText,CreatedAt=@CreatedAt WHERE Id=@Id";
                    AddOrderParameters(cmd, r);
                }
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static void AddOrderParameters(SqliteCommand cmd, DataRow r)
    {
        cmd.Parameters.AddWithValue("@Id", r["Id"]);
        cmd.Parameters.AddWithValue("@CustomerId", r["CustomerId"]);
        cmd.Parameters.AddWithValue("@Amount", r["Amount"]);
        cmd.Parameters.AddWithValue("@PaymentMethod", r["PaymentMethod"]);
        cmd.Parameters.AddWithValue("@Priority", r["Priority"]);
        cmd.Parameters.AddWithValue("@Status", r["Status"]);
        cmd.Parameters.AddWithValue("@DecisionCode", r["DecisionCode"]);
        cmd.Parameters.AddWithValue("@DecisionText", r["DecisionText"]);
        cmd.Parameters.AddWithValue("@CreatedAt", ToDbDate(Convert.ToDateTime(r["CreatedAt"])));
    }

    private static void SaveAudit(SqliteConnection cn, SqliteTransaction tx, DataTable table)
    {
        int i;
        for (i = 0; i < table.Rows.Count; i++)
        {
            DataRow r = table.Rows[i];
            if (r.RowState != DataRowState.Added) continue;
            using (SqliteCommand cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO Audit(Id,At,Type,Subject,Detail) VALUES(@Id,@At,@Type,@Subject,@Detail)";
                cmd.Parameters.AddWithValue("@Id", r["Id"]);
                cmd.Parameters.AddWithValue("@At", ToDbDate(Convert.ToDateTime(r["At"])));
                cmd.Parameters.AddWithValue("@Type", r["Type"]);
                cmd.Parameters.AddWithValue("@Subject", r["Subject"]);
                cmd.Parameters.AddWithValue("@Detail", r["Detail"]);
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static int ScalarInt(string sql)
    {
        using (SqliteConnection cn = OpenConnection())
        using (SqliteCommand cmd = cn.CreateCommand())
        {
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private static void SeedDatabase()
    {
        using (SqliteConnection cn = OpenConnection())
        using (SqliteTransaction tx = cn.BeginTransaction())
        {
            InsertCustomer(cn, tx, "C001", "Ferretería Andévalo", "ES", new DateTime(2001, 4, 12), 12000m, 900m, 0, 18, false, false);
            InsertCustomer(cn, tx, "C002", "Comercial Atlántico", "PT", new DateTime(2012, 9, 2), 9000m, 6400m, 42, 61, false, false);
            InsertCustomer(cn, tx, "C003", "Suministros Mineros Sur", "ES", new DateTime(2007, 1, 8), 18000m, 15100m, 118, 86, true, false);
            InsertCustomer(cn, tx, "C004", "Talleres Condado", "ES", new DateTime(2023, 3, 17), 4500m, 350m, 0, 39, false, false);
            InsertCustomer(cn, tx, "C005", "Distribuzioni Mare", "IT", new DateTime(1999, 11, 20), 25000m, 5900m, 12, 48, false, false);
            InsertOrder(cn, tx, "ORD-SEED-01", "C002", 1450m, "INVOICE", "NORMAL", "REVIEW", "R42", "Existing exposure review", DateTime.Now.AddDays(-8));
            InsertOrder(cn, tx, "ORD-SEED-02", "C001", 620m, "TRANSFER", "NORMAL", "APPROVED", "A00", "Approved", DateTime.Now.AddDays(-2));
            using (SqliteCommand cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO Audit(Id,At,Type,Subject,Detail) VALUES(@Id,@At,'SYSTEM','BOOTSTRAP','Embedded SQLite database created')";
                cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("@At", ToDbDate(DateTime.Now));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static void InsertCustomer(SqliteConnection cn, SqliteTransaction tx, string id, string name, string country, DateTime since, decimal creditLimit, decimal debt, int overdue, int risk, bool blocked, bool manualOverride)
    {
        using (SqliteCommand cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO Customers(Id,Name,Country,Since,CreditLimit,OutstandingDebt,OverdueDays,RiskScore,Blocked,ManualOverride) VALUES(@Id,@Name,@Country,@Since,@CreditLimit,@Debt,@Overdue,@Risk,@Blocked,@Override)";
            cmd.Parameters.AddWithValue("@Id", id); cmd.Parameters.AddWithValue("@Name", name); cmd.Parameters.AddWithValue("@Country", country); cmd.Parameters.AddWithValue("@Since", ToDbDate(since));
            cmd.Parameters.AddWithValue("@CreditLimit", creditLimit); cmd.Parameters.AddWithValue("@Debt", debt); cmd.Parameters.AddWithValue("@Overdue", overdue); cmd.Parameters.AddWithValue("@Risk", risk); cmd.Parameters.AddWithValue("@Blocked", blocked ? 1 : 0); cmd.Parameters.AddWithValue("@Override", manualOverride ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertOrder(SqliteConnection cn, SqliteTransaction tx, string id, string customerId, decimal amount, string payment, string priority, string status, string code, string text, DateTime created)
    {
        using (SqliteCommand cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO Orders(Id,CustomerId,Amount,PaymentMethod,Priority,Status,DecisionCode,DecisionText,CreatedAt) VALUES(@Id,@CustomerId,@Amount,@Payment,@Priority,@Status,@Code,@Text,@CreatedAt)";
            cmd.Parameters.AddWithValue("@Id", id); cmd.Parameters.AddWithValue("@CustomerId", customerId); cmd.Parameters.AddWithValue("@Amount", amount); cmd.Parameters.AddWithValue("@Payment", payment); cmd.Parameters.AddWithValue("@Priority", priority); cmd.Parameters.AddWithValue("@Status", status); cmd.Parameters.AddWithValue("@Code", code); cmd.Parameters.AddWithValue("@Text", text); cmd.Parameters.AddWithValue("@CreatedAt", ToDbDate(created));
            cmd.ExecuteNonQuery();
        }
    }

    private static string ToDbDate(DateTime value)
    {
        return value.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DataSet BuildSchema()
    {
        DataSet ds = new DataSet("LegacyCreditDesk");
        DataTable customers = new DataTable("Customers");
        customers.Columns.Add("Id", typeof(string)); customers.Columns.Add("Name", typeof(string)); customers.Columns.Add("Country", typeof(string)); customers.Columns.Add("Since", typeof(DateTime));
        customers.Columns.Add("CreditLimit", typeof(decimal)); customers.Columns.Add("OutstandingDebt", typeof(decimal)); customers.Columns.Add("OverdueDays", typeof(int)); customers.Columns.Add("RiskScore", typeof(int));
        customers.Columns.Add("Blocked", typeof(bool)); customers.Columns.Add("ManualOverride", typeof(bool)); customers.PrimaryKey = new DataColumn[] { customers.Columns["Id"] }; ds.Tables.Add(customers);

        DataTable orders = new DataTable("Orders");
        orders.Columns.Add("Id", typeof(string)); orders.Columns.Add("CustomerId", typeof(string)); orders.Columns.Add("Amount", typeof(decimal)); orders.Columns.Add("PaymentMethod", typeof(string));
        orders.Columns.Add("Priority", typeof(string)); orders.Columns.Add("Status", typeof(string)); orders.Columns.Add("DecisionCode", typeof(string)); orders.Columns.Add("DecisionText", typeof(string)); orders.Columns.Add("CreatedAt", typeof(DateTime)); ds.Tables.Add(orders);

        DataTable audit = new DataTable("Audit");
        audit.Columns.Add("Id", typeof(string)); audit.Columns.Add("At", typeof(DateTime)); audit.Columns.Add("Type", typeof(string)); audit.Columns.Add("Subject", typeof(string)); audit.Columns.Add("Detail", typeof(string)); ds.Tables.Add(audit);
        return ds;
    }

    private static string Escape(string value) { return (value ?? "").Replace("'", "''"); }

    private const string SchemaSql = @"
CREATE TABLE IF NOT EXISTS Customers(
 Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Country TEXT NOT NULL, Since TEXT NOT NULL,
 CreditLimit NUMERIC NOT NULL, OutstandingDebt NUMERIC NOT NULL DEFAULT 0,
 OverdueDays INTEGER NOT NULL DEFAULT 0, RiskScore INTEGER NOT NULL DEFAULT 0,
 Blocked INTEGER NOT NULL DEFAULT 0, ManualOverride INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS Orders(
 Id TEXT PRIMARY KEY, CustomerId TEXT NOT NULL, Amount NUMERIC NOT NULL,
 PaymentMethod TEXT NOT NULL, Priority TEXT NOT NULL, Status TEXT NOT NULL,
 DecisionCode TEXT NOT NULL, DecisionText TEXT NOT NULL, CreatedAt TEXT NOT NULL,
 FOREIGN KEY(CustomerId) REFERENCES Customers(Id));
CREATE TABLE IF NOT EXISTS Audit(
 Id TEXT PRIMARY KEY, At TEXT NOT NULL, Type TEXT NOT NULL, Subject TEXT NOT NULL, Detail TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS IX_Orders_Customer_Status ON Orders(CustomerId,Status);
CREATE INDEX IF NOT EXISTS IX_Orders_CreatedAt ON Orders(CreatedAt);
CREATE VIEW IF NOT EXISTS vw_CustomerExposure AS
 SELECT c.Id,c.Name,c.CreditLimit,c.OutstandingDebt,
        COALESCE(SUM(CASE WHEN o.Status<>'BLOCKED' THEN o.Amount ELSE 0 END),0) AS OpenOrders,
        c.OutstandingDebt + COALESCE(SUM(CASE WHEN o.Status<>'BLOCKED' THEN o.Amount ELSE 0 END),0) AS TotalExposure
 FROM Customers c LEFT JOIN Orders o ON o.CustomerId=c.Id GROUP BY c.Id,c.Name,c.CreditLimit,c.OutstandingDebt;
CREATE VIEW IF NOT EXISTS vw_ManualReviewQueue AS
 SELECT o.Id,o.CustomerId,c.Name,o.Amount,o.DecisionCode,o.DecisionText,o.CreatedAt
 FROM Orders o JOIN Customers c ON c.Id=o.CustomerId WHERE o.Status='REVIEW';
CREATE TRIGGER IF NOT EXISTS trg_OrderBlockedAudit AFTER INSERT ON Orders WHEN NEW.Status='BLOCKED'
BEGIN
 INSERT INTO Audit(Id,At,Type,Subject,Detail)
 VALUES(lower(hex(randomblob(16))),datetime('now'),'DB_TRIGGER',NEW.Id,'Blocked order inserted; decision=' || NEW.DecisionCode);
END;";
}
