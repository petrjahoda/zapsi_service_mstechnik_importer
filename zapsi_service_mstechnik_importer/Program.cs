using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;

namespace zapsi_service_mstechnik_importer {
    class Program {
        private const string BuildDate = "2019.2.3.11";
        private const string DataFolder = "Logs";
        private const string RedColor = "\u001b[31;1m";
        private const string YellowColor = "\u001b[33;1m";
        private const string CyanColor = "\u001b[36;1m";
        private const string ResetColor = "\u001b[0m";

        private static bool _osIsLinux;
        private static bool _loopIsRunning;

        private static string _ipAddress;
        private static string _database;
        private static string _port;
        private static string _login;
        private static string _password;
        private static string _customer;
        private static string _email;
        private static string _downloadEvery;
        private static string _deleteFilesAfterDays;
        private static string _smtpClient;
        private static string _smtpPort;
        private static string _smtpUsername;
        private static string _smtpPassword;

        private const double InitialDownloadValue = 1000;


        static void Main() {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                Console.WriteLine(CyanColor + "  >> MSTECHNIK IMPORTER ");
            } else {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("  >> MSTECHNIK IMPORTER ");
            }
            var outputPath = CreateLogFileIfNotExists("0-main.txt");
            using (CreateLogger(outputPath, out var logger)) {
                CheckOsPlatform(logger);
                LogInfo("[ MAIN ] --INF-- Program built at: " + BuildDate, logger);
                CreateConfigFileIfNotExists(logger);
                LoadSettingsFromConfigFile(logger);
                SendEmail("Computer: " + Environment.MachineName + ", User: " + Environment.UserName + ", Program started at " + DateTime.Now + ", Version " + BuildDate, logger);
                var timer = new System.Timers.Timer(InitialDownloadValue);
                timer.Elapsed += (sender, e) => {
                    timer.Interval = Convert.ToDouble(_downloadEvery);
                    if (!_loopIsRunning) {
                        _loopIsRunning = true;
                        LogInfo($"[ MAIN ] --INF-- Transferring users", logger);
                        TransferUsers(logger);
                        LogInfo($"[ MAIN ] --INF-- Transferring products", logger);
                        TransferProducts(logger);
                        LogInfo($"[ MAIN ] --INF-- Transferring orders", logger);
                        TransferOrders(logger);
                        LogInfo($"[ MAIN ] --INF-- Deleting old log data", logger);
                        DeleteOldLogFiles(logger);
                        _loopIsRunning = false;
                        LogInfo($"[ MAIN ] --INF-- Complete, waiting for another run in about a minute" , logger);
                    }
                };
                RunTimer(timer);
            }
        }

        private static void DeleteOldLogFiles(ILogger logger) {
            var currentDirectory = Directory.GetCurrentDirectory();
            var outputPath = Path.Combine(currentDirectory, DataFolder);
            try {
                Directory.GetFiles(outputPath)
                    .Select(f => new FileInfo(f))
                    .Where(f => f.CreationTime < DateTime.Now.AddDays(Convert.ToDouble(_deleteFilesAfterDays)))
                    .ToList()
                    .ForEach(f => f.Delete());
                LogInfo("[ MAIN ] --INF-- Cleared old files.", logger);
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- Problem clearing old log files: " + error.Message, logger);
            }
        }

        private static void TransferOrders(ILogger logger) {
            LogInfo($"[ MAIN ] --INF-- Downloading orders from external database", logger);
            var mstechnikorders = DownloadActualOrdersFromExternalDatabase(logger);
            LogInfo($"[ MAIN ] --INF-- Downloading orders from Zapsi database", logger);
            var zapsiOrders = DownloadActualOrdersFromZapsiDatabase(logger);
            LogInfo($"[ MAIN ] --INF-- Comparing orders: " + mstechnikorders.Count + "-" + zapsiOrders.Count, logger);
            foreach (var order in mstechnikorders) {
                if (!zapsiOrders.Contains(order.Name)) {
                    LogInfo($"[  {order.Oid} ] --INF-- Adding new order: " + order.Name, logger);
                    CreateNewOrderInZapsi(order, logger);
                }
            }
        }

        private static void CreateNewOrderInZapsi(Order order, ILogger logger) {
            var productId = GetProductIdFromZapsiProductTable(order.ProductName, order.Cycle, logger);
            var connection = new MySqlConnection($"server={_ipAddress};port={_port};userid={_login};password={_password};database={_database};");
            try {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = $"INSERT INTO `zapsi2`.`order` (`Name`, `Barcode`, `ProductID`, `OrderStatusID`, `CountRequested`, `WorkplaceID`) " +
                                      $"VALUES ('{order.Name}', '{order.Barcode}', {productId}, DEFAULT, {order.RequestedAmount}, NULL);";
                try {
                    command.ExecuteNonQuery();
                    LogInfo($"[  {order.Oid} ] --INF-- Added from external database to Zapsi", logger);
                } catch (Exception error) {
                    LogError($"[ {order.Oid} ] --ERR-- Problem inserting into database: {error.Message}{command.CommandText}", logger);
                } finally {
                    command.Dispose();
                }

                connection.Close();
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- Problem with database: " + error.Message, logger);
            } finally {
                connection.Dispose();
            }
        }

        private static int GetProductIdFromZapsiProductTable(string orderProductName, string orderProductCycle, ILogger logger) {
            var productId = 1;
            var connection = new MySqlConnection($"server={_ipAddress};port={_port};userid={_login};password={_password};database={_database};");
            try {
                connection.Open();
                var selectQuery = $"select * from zapsi2.product where Name like '{orderProductName}' and Cycle like '{orderProductCycle}'";
                var command = new MySqlCommand(selectQuery, connection);
                try {
                    var reader = command.ExecuteReader();
                    while (reader.Read()) {
                        productId = Convert.ToInt32(reader["Oid"]);
                    }
                    reader.Close();
                    reader.Dispose();
                } catch (Exception error) {
                    LogError("[ MAIN ] --ERR-- Problem reading product for order: " + error.Message + selectQuery, logger);
                } finally {
                    command.Dispose();
                }

                connection.Close();
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- Problem with database: " + error.Message, logger);
            } finally {
                connection.Dispose();
            }
            return productId;
        }


        private static List<string> DownloadActualOrdersFromZapsiDatabase(ILogger logger) {
            var orderOiDs = new List<string>();
            var connection = new MySqlConnection($"server={_ipAddress};port={_port};userid={_login};password={_password};database={_database};");
            try {
                connection.Open();
                var selectQuery = $"select * from zapsi2.order";
                var command = new MySqlCommand(selectQuery, connection);
                try {
                    var reader = command.ExecuteReader();
                    while (reader.Read()) {
                        var actualOid = Convert.ToString(reader["Name"]);
                        orderOiDs.Add(actualOid);
                    }
                    reader.Close();
                    reader.Dispose();
                } catch (Exception error) {
                    LogError("[ MAIN ] --ERR-- Problem reading orders table: " + error.Message + selectQuery, logger);
                } finally {
                    command.Dispose();
                }

                connection.Close();
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- Problem with database: " + error.Message, logger);
            } finally {
                connection.Dispose();
            }
            return orderOiDs;
        }

        private static List<Order> DownloadActualOrdersFromExternalDatabase(ILogger logger) {
            var orders = new List<Order>();
            var connection = new SqlConnection {ConnectionString = @"Data Source=192.168.170.3\qiserver; Initial Catalog=QI; User id=zapsi; Password=12MSzap*;"};

            try {
                connection.Open();
                var selectQuery = $"select * from Zapsi_ML";
                var command = new SqlCommand(selectQuery, connection);
                try {
                    var reader = command.ExecuteReader();
                    while (reader.Read()) {
                        var order = new Order();
                        order.Name = Convert.ToString(reader["ML"]);
                        order.Barcode = Convert.ToString(reader["ML"]);
                        order.RequestedAmount = Convert.ToString(reader["MNOZ_POZADAVEK"]);
                        order.ProductName = Convert.ToString(reader["CIS_ZB"]);
                        order.Cycle =Convert.ToString(reader["CAS_KUS"]);
                        orders.Add(order);
                    }
                    reader.Close();
                    reader.Dispose();
                } catch (Exception error) {
                    LogError("[ MAIN ] --ERR-- Problem getting order from external database " + error.Message + selectQuery, logger);
                } finally {
                    command.Dispose();
                }

                connection.Close();
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- problem with database: " + error.Message, logger);
            } finally {
                connection.Dispose();
            }
            return orders;
        }


        private static void TransferProducts(ILogger logger) {
            LogInfo($"[ MAIN ] --INF-- Downloading products from external database", logger);
            var mstechnikProducts = DownloadActualProductsFromExternalDatabase(logger);
            LogInfo($"[ MAIN ] --INF-- Downloading products from Zapsi database", logger);
            var zapsiProducts = DownloadActualProductsFromZapsiDatabase(logger);
            LogInfo($"[ MAIN ] --INF-- Comparing products: " + mstechnikProducts.Count + "-" + zapsiProducts.Count, logger);
            foreach (var product in mstechnikProducts) {
                if (!zapsiProducts.Contains(product.Name)) {
                    CreateNewProductInZapsi(product, logger);
                }
            }
        }

        private static void CreateNewProductInZapsi(Product product, ILogger logger) {
            var connection = new MySqlConnection($"server={_ipAddress};port={_port};userid={_login};password={_password};database={_database};");
            try {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = $"INSERT INTO `zapsi2`.`product` (`Name`, `Barcode`, `Cycle`, `IdleFromTime`, `ProductStatusID`, `Deleted`, `ProductGroupID`) " +
                                      $"VALUES ('{product.Name}', '{product.Name}', '{product.Cycle}', '30', DEFAULT, DEFAULT, null);";
                try {
                    command.ExecuteNonQuery();
                    LogInfo($"[  {product.Name} ] --INF-- Added from external database to Zapsi database", logger);
                } catch (Exception error) {
                    LogError($"[ {product.Name} ] --ERR-- Problem inserting into database: {error.Message}{command.CommandText}", logger);
                } finally {
                    command.Dispose();
                }

                connection.Close();
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- Problem with database: " + error.Message, logger);
            } finally {
                connection.Dispose();
            }
        }

        private static List<string> DownloadActualProductsFromZapsiDatabase(ILogger logger) {
            var productBarcodeList = new List<string>();
            var connection = new MySqlConnection($"server={_ipAddress};port={_port};userid={_login};password={_password};database={_database};");
            try {
                connection.Open();
                var selectQuery = $"select * from zapsi2.product";
                var command = new MySqlCommand(selectQuery, connection);
                try {
                    var reader = command.ExecuteReader();
                    while (reader.Read()) {
                        var name = Convert.ToString(reader["Name"]);
                        productBarcodeList.Add(name);
                    }
                    reader.Close();
                    reader.Dispose();
                } catch (Exception error) {
                    LogError("[ MAIN ] --ERR-- Problem reading product table: " + error.Message + selectQuery, logger);
                } finally {
                    command.Dispose();
                }

                connection.Close();
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- Problem with database: " + error.Message, logger);
            } finally {
                connection.Dispose();
            }
            return productBarcodeList;
        }

        private static List<Product> DownloadActualProductsFromExternalDatabase(ILogger logger) {
            var products = new List<Product>();
            var connection = new SqlConnection {ConnectionString = @"Data Source=192.168.170.3\qiserver; Initial Catalog=QI; User id=zapsi; Password=12MSzap*;"};

            try {
                connection.Open();
                var selectQuery = $"select * from Zapsi_ML";
                var command = new SqlCommand(selectQuery, connection);
                try {
                    var reader = command.ExecuteReader();
                    while (reader.Read()) {
                        var product = new Product();
                        product.Name = Convert.ToString(reader["CIS_ZB"]);
                        product.Cycle = Convert.ToString(reader["CAS_KUS"]);
                        products.Add(product);
                    }
                    reader.Close();
                    reader.Dispose();
                } catch (Exception error) {
                    LogError("[ MAIN ] --ERR-- Problem getting product from external database " + error.Message + selectQuery, logger);
                } finally {
                    command.Dispose();
                }

                connection.Close();
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- problem with database: " + error.Message, logger);
            } finally {
                connection.Dispose();
            }
            return products;
        }

        private static void TransferUsers(ILogger logger) {
            LogInfo($"[ MAIN ] --INF-- Downloading users from external database", logger);
            var mstechnikUsers = DownloadActualUsersFromOtherDatabase(logger);
            LogInfo($"[ MAIN ] --INF-- Downloading users from Zapsi database", logger);
            var zapsiUsers = DownloadActualUsersFromZapsiDatabase(logger);
            LogInfo($"[ MAIN ] --INF-- Comparing users: " + mstechnikUsers.Count() + "-" + zapsiUsers.Count(), logger);
            foreach (var user in mstechnikUsers) {
                if (!zapsiUsers.Contains(user.Login)) {
                    CreateNewUserInZapsi(user, logger);
                }
            }
        }

        private static void CreateNewUserInZapsi(User user, ILogger logger) {
            var numberToInsert = user.Login;
            var connection = new MySqlConnection($"server={_ipAddress};port={_port};userid={_login};password={_password};database={_database};");
            try {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText =
                    $"INSERT INTO `zapsi2`.`user` (`Login`, `Password`, `Name`, `FirstName`, `Rfid`, `Barcode`, `Pin`, `Function`, `UserTypeID`, `Email`, `Phone`, `UserRoleID`)" +
                    $" VALUES ('{numberToInsert}', null, '{user.Surname}', '{user.FirstName}', '{numberToInsert}', '{numberToInsert}', '{numberToInsert}', null, {user.UserTypeId}, null, null, 2);";
                LogInfo(command.CommandText, logger);
                try {
                    command.ExecuteNonQuery();
                    LogInfo($"[ {user.FirstName} {user.Surname} ] --INF-- Added from external database to Zapsi", logger);
                } catch (Exception error) {
                    LogError($"[ {user.FirstName} {user.Surname} ] --ERR-- Problem inserting into database: {error.Message}{command.CommandText}", logger);
                } finally {
                    command.Dispose();
                }

                connection.Close();
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- Problem with database: " + error.Message, logger);
            } finally {
                connection.Dispose();
            }
        }

        private static List<string> DownloadActualUsersFromZapsiDatabase(ILogger logger) {
            var userOidList = new List<string>();
            var connection = new MySqlConnection($"server={_ipAddress};port={_port};userid={_login};password={_password};database={_database};");
            try {
                connection.Open();
                var selectQuery = $"select * from zapsi2.user";
                var command = new MySqlCommand(selectQuery, connection);
                try {
                    var reader = command.ExecuteReader();
                    while (reader.Read()) {
                        var actualOid = Convert.ToString(reader["Login"]);
                        userOidList.Add(actualOid);
                    }
                    reader.Close();
                    reader.Dispose();
                } catch (Exception error) {
                    LogError("[ MAIN ] --ERR-- Problem reading users from zapsi database: " + error.Message + selectQuery, logger);
                } finally {
                    command.Dispose();
                }

                connection.Close();
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- Problem with database: " + error.Message, logger);
            } finally {
                connection.Dispose();
            }
            return userOidList;
        }

        private static IEnumerable<User> DownloadActualUsersFromOtherDatabase(ILogger logger) {
            var users = new List<User>();
            var connection = new SqlConnection {ConnectionString = @"Data Source=192.168.170.3\qiserver; Initial Catalog=QI; User id=zapsi; Password=12MSzap*;"};

            try {
                connection.Open();
                string selectQuery = $"select * from Zapsi_Lidi";
                var command = new SqlCommand(selectQuery, connection);
                try {
                    var reader = command.ExecuteReader();
                    while (reader.Read()) {
                        var user = new User();
                        user.Login = Convert.ToString(reader["CISLO"]);
                        user.FirstName = Convert.ToString(reader["JMENO"]);
                        user.Surname = Convert.ToString(reader["PRIJMENI"]);
                        var userType = Convert.ToString(reader["ZARAZENI"]);
                        if (userType.Contains("Oper")) {
                            user.UserTypeId = 1;
                        } else {
                            user.UserTypeId = 2;
                        }
                        users.Add(user);
                    }
                    reader.Close();
                    reader.Dispose();
                } catch (Exception error) {
                    LogError("[ MAIN ] --ERR-- Problem getting user from external database " + error.Message + selectQuery, logger);
                } finally {
                    command.Dispose();
                }

                connection.Close();
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- problem with database: " + error.Message, logger);
            } finally {
                connection.Dispose();
            }
            return users;
        }

        private static void RunTimer(System.Timers.Timer timer) {
            timer.Start();
            while (timer.Enabled) {
                Thread.Sleep(Convert.ToInt32(_downloadEvery));
                var text = "[ MAIN ] --INF-- Program still running.";
                var now = DateTime.Now;
                text = now + " " + text;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                    Console.WriteLine(CyanColor + text + ResetColor);
                } else {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine(text);
                    Console.ForegroundColor = ConsoleColor.White;
                }
            }

            timer.Stop();
            timer.Dispose();
        }

        private static void SendEmail(string dataToSend, ILogger logger) {
            ServicePointManager.ServerCertificateValidationCallback = RemoteServerCertificateValidationCallback;
            var client = new SmtpClient(_smtpClient) {
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(_smtpUsername, _smtpPassword),
                Port = int.Parse(_smtpPort)
            };
            var mailMessage = new MailMessage {From = new MailAddress(_smtpUsername)};
            mailMessage.To.Add(_email);
            mailMessage.Subject = "MSTECHNIK IMPORTER >> " + _customer;
            mailMessage.Body = dataToSend;
            client.EnableSsl = true;
            try {
                client.Send(mailMessage);
                LogInfo("[ MAIN ] --INF-- Email sent: " + dataToSend, logger);
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- Cannot send email: " + dataToSend + ": " + error.Message, logger);
            }
        }

        private static bool RemoteServerCertificateValidationCallback(object sender, System.Security.Cryptography.X509Certificates.X509Certificate certificate,
            System.Security.Cryptography.X509Certificates.X509Chain chain, System.Net.Security.SslPolicyErrors sslPolicyErrors) {
            return true;
        }

        private static void LoadSettingsFromConfigFile(ILogger logger) {
            var currentDirectory = Directory.GetCurrentDirectory();
            const string configFile = "config.json";
            const string backupConfigFile = "config.json.backup";
            var outputPath = Path.Combine(currentDirectory, configFile);
            var backupOutputPath = Path.Combine(currentDirectory, backupConfigFile);
            var configFileLoaded = false;
            try {
                var configBuilder = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("config.json");
                var configuration = configBuilder.Build();
                _ipAddress = configuration["ipaddress"];
                _database = configuration["database"];
                _port = configuration["port"];
                _login = configuration["login"];
                _password = configuration["password"];
                _customer = configuration["customer"];
                _email = configuration["email"];
                _downloadEvery = configuration["downloadevery"];
                _deleteFilesAfterDays = configuration["deletefilesafterdays"];
                _smtpClient = configuration["smtpclient"];
                _smtpPort = configuration["smtpport"];
                _smtpUsername = configuration["smtpusername"];
                _smtpPassword = configuration["smtppassword"];
                LogInfo("[ MAIN ] --INF-- Config loaded from file for customer: " + _customer, logger);

                configFileLoaded = true;
            } catch (Exception error) {
                LogError("[ MAIN ] --ERR-- Cannot load config from file: " + error.Message, logger);
            }

            if (!configFileLoaded) {
                LogInfo("[ MAIN ] --INF-- Loading backup file.", logger);
                File.Delete(outputPath);
                File.Copy(backupOutputPath, outputPath);
                LogInfo("[ MAIN ] --INF-- Config file updated from backup file.", logger);
                LoadSettingsFromConfigFile(logger);
            }
        }

        private static void CreateConfigFileIfNotExists(ILogger logger) {
            var currentDirectory = Directory.GetCurrentDirectory();
            const string configFile = "config.json";
            const string backupConfigFile = "config.json.backup";
            var outputPath = Path.Combine(currentDirectory, configFile);
            var backupOutputPath = Path.Combine(currentDirectory, backupConfigFile);
            var config = new Config();
            if (!File.Exists(outputPath)) {
                var dataToWrite = JsonConvert.SerializeObject(config);
                try {
                    File.WriteAllText(outputPath, dataToWrite);
                    LogInfo("[ MAIN ] --INF-- Config file created.", logger);
                    if (File.Exists(backupOutputPath)) {
                        File.Delete(backupOutputPath);
                    }

                    File.WriteAllText(backupOutputPath, dataToWrite);
                    LogInfo("[ MAIN ] --INF-- Backup file created.", logger);
                } catch (Exception error) {
                    LogError("[ MAIN ] --ERR-- Cannot create config or backup file: " + error.Message, logger);
                }
            } else {
                LogInfo("[ MAIN ] --INF-- Config file already exists.", logger);
            }
        }

        private static void CheckOsPlatform(ILogger logger) {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                _osIsLinux = true;
                LogInfo("[ MAIN ] --INF-- OS Linux, disable logging to file", logger);
            } else {
                _osIsLinux = false;
            }
        }

        private static void LogInfo(string text, ILogger logger) {
            var now = DateTime.Now;
            text = now + " " + text;
            if (_osIsLinux) {
                Console.WriteLine(CyanColor + text + ResetColor);
            } else {
                logger.LogInformation(text);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(text);
                Console.ForegroundColor = ConsoleColor.White;
            }
        }


        private static void LogError(string text, ILogger logger) {
            var now = DateTime.Now;
            text = now + " " + text;
            if (_osIsLinux) {
                Console.WriteLine(YellowColor + text + ResetColor);
            } else {
                logger.LogInformation(text);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(text);
                Console.ForegroundColor = ConsoleColor.White;
            }
        }

        private static LoggerFactory CreateLogger(string outputPath, out ILogger logger) {
            var factory = new LoggerFactory();
            logger = factory.CreateLogger("Alarm Server Core");
            factory.AddFile(outputPath, LogLevel.Debug);
            return factory;
        }

        private static string CreateLogFileIfNotExists(string fileName) {
            var currentDirectory = Directory.GetCurrentDirectory();
            var logFilename = fileName;
            var outputPath = Path.Combine(currentDirectory, DataFolder, logFilename);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            CreateLogDirectoryIfNotExists(outputDirectory);
            return outputPath;
        }

        private static void CreateLogDirectoryIfNotExists(string outputDirectory) {
            if (!Directory.Exists(outputDirectory)) {
                try {
                    Directory.CreateDirectory(outputDirectory);
                    var text = "[ MAIN ] --INF-- Log directory created.";
                    var now = DateTime.Now;
                    text = now + " " + text;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                        Console.WriteLine(CyanColor + text + ResetColor);
                    } else {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine(text);
                        Console.ForegroundColor = ConsoleColor.White;
                    }
                } catch (Exception error) {
                    var text = "[ MAIN ] --ERR-- Log directory not created: " + error.Message;
                    var now = DateTime.Now;
                    text = now + " " + text;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                        Console.WriteLine(RedColor + text + ResetColor);
                    } else {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(text);
                        Console.ForegroundColor = ConsoleColor.White;
                    }
                }
            }
        }
    }
}
