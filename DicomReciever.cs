using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Dicom.Imaging.Codec;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;
using DicomTranscoder = FellowOakDicom.Imaging.Codec.DicomTranscoder;

namespace smaro_scp_app
{
    internal class DicomReceiver : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly string _orthancApiUrl = "https://api.smaro.app/api/console/orthanc/upload";
        private static readonly string _logFolder = @"C:\logs\smaro\";

        public DicomReceiver(INetworkStream stream, Encoding fallbackEncoding, ILogger logger, DicomServiceDependencies dependencies)
            : base(stream, fallbackEncoding, logger, dependencies)
        {
            // Ensure log directory exists
            Directory.CreateDirectory(_logFolder);

            // Clean up old log files
            CleanupOldLogs();
        }

        public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            // Log the association request details for debugging
            Log($"Received Association Request from {association.CallingAE} to {association.CalledAE}");

            foreach (var context in association.PresentationContexts)
            {
                // Accept any requested Abstract Syntax
                context.SetResult(DicomPresentationContextResult.Accept);
            }

            // Send the Association Accept response
            try
            {
                await SendAssociationAcceptAsync(association);
                Log($"Association accepted for {association.CallingAE}.");
            }
            catch (Exception ex)
            {
                Log($"Error while sending Association Accept: {ex.Message}");
            }
        }


        public  Task OnReceiveAssociationReleaseRequestAsync()
        {
            Log("Received Association Release Request");
            return SendAssociationReleaseResponseAsync();
        }
        public  void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        {
            Log($"Received Abort: Source = {source}, Reason = {reason}");
            Application.Current.Dispatcher.Invoke(() => MessageBox.Show($"Received abort: {reason}"));
        }

        public  void OnConnectionClosed(Exception exception)
        {
            Log($"Connection Closed: {exception?.Message}");
        }

        public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
        {
            Log("Received C-ECHO Request");
            return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
        }

        public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
        {
            var file = request.File;
            var fileFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"smaro_scp_app", "dicoms");
            Directory.CreateDirectory(fileFolderPath);

            var filePath = Path.Combine(fileFolderPath, file.FileMetaInfo.MediaStorageSOPInstanceUID.UID + ".dcm");
            var convertedFilePath = Path.Combine(fileFolderPath, file.FileMetaInfo.MediaStorageSOPInstanceUID.UID + "_converted.dcm");
            var compressedFilePath = Path.Combine(fileFolderPath, file.FileMetaInfo.MediaStorageSOPInstanceUID.UID + "_compressed.dcm");

            // Save the incoming file
            file.Save(filePath);
            Log($"DICOM file received and saved: {filePath}");
            SaveDicomMetadataToDatabase(file, compressedFilePath, "In Progress");
            Application.Current.Dispatcher.Invoke(() => ViewReportsWindow.UpdateDataGrid?.Invoke());


            try
            {
                // Convert to required transfer syntax
                ConvertToRequiredTransferSyntax(filePath, convertedFilePath);
                CompressDicomFile(convertedFilePath, compressedFilePath);
                

                // Proceed with further processing using the converted file
                Application.Current.Dispatcher.Invoke(() => ViewReportsWindow.UpdateDataGrid?.Invoke());

                var (branchId, clientId) = GetAuthDetails();
                bool isSent = await SendDicomFileToOrthanc(compressedFilePath, branchId, clientId);
                if (isSent)
                {
                    UpdateStatusInDatabase(compressedFilePath, "Completed");
                    Application.Current.Dispatcher.Invoke(() => ViewReportsWindow.UpdateDataGrid?.Invoke());

                    File.Delete(filePath);
                    File.Delete(convertedFilePath);
                    File.Delete(compressedFilePath);
                    Log("DICOM file successfully sent to Orthanc and deleted locally.");
                }
                else
                {
                    File.Delete(filePath);
                    File.Delete(convertedFilePath);
                    UpdateStatusInDatabase(convertedFilePath, "Failed");
                    Application.Current.Dispatcher.Invoke(() => ViewReportsWindow.UpdateDataGrid?.Invoke());
                    Log("Failed to send DICOM file to Orthanc.");
                }
            }
            catch (Exception ex)
            {
                File.Delete(filePath);
                File.Delete(convertedFilePath);
                UpdateStatusInDatabase(convertedFilePath, "Failed");
                Log($"Error processing DICOM file: {ex.Message}");
            }

            return new DicomCStoreResponse(request, DicomStatus.Success);
        }


        public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
        {
            Log($"C-STORE Request Exception for {tempFileName}: {e.Message}");
            Application.Current.Dispatcher.Invoke(() => MessageBox.Show($"C-STORE request failed for {tempFileName}: {e.Message}"));
            return Task.CompletedTask;
        }

        private async Task<bool> SendDicomFileToOrthanc(string filePath, int branchId, int clientId)
        {
            try
            {
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    var content = new MultipartFormDataContent();
                    var dicomContent = new ByteArrayContent(await ReadFully(fileStream));
                    dicomContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dicom");
                    content.Add(dicomContent, "file", Path.GetFileName(filePath));
                    content.Add(new StringContent(branchId.ToString()), "branch_id");
                    content.Add(new StringContent(clientId.ToString()), "client_id");

                    var response = await _httpClient.PostAsync(_orthancApiUrl, content);
                    if (response.IsSuccessStatusCode)
                    {
                        Log($"File successfully uploaded to Orthanc: {filePath}");
                        return true;
                    }
                    else
                    {
                        Log($"Failed to upload file to Orthanc: {filePath}, Response: {response.StatusCode}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Exception while sending file to Orthanc: {ex.Message}");
                return false;
            }
        }

// Helper method to read the full file into a byte array
        private async Task<byte[]> ReadFully(Stream stream)
        {
            using (var memoryStream = new MemoryStream())
            {
                await stream.CopyToAsync(memoryStream);
                return memoryStream.ToArray();
            }
        }

        private void Log(string message)
        {
            string logFilePath = Path.Combine(_logFolder, $"smaro_{DateTime.Now:yyyyMMdd}.log");
            string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}";
            File.AppendAllText(logFilePath, logMessage);
        }

        private void CleanupOldLogs()
        {
            try
            {
                var logFiles = Directory.GetFiles(_logFolder, "smaro_*.log");
                foreach (var file in logFiles)
                {
                    var creationTime = File.GetCreationTime(file);
                    if (creationTime < DateTime.Now.AddDays(-7))
                    {
                        File.Delete(file);
                        Log($"Deleted old log file: {file}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error during log cleanup: {ex.Message}");
            }
        }


private void SaveDicomMetadataToDatabase(DicomFile dicomFile, string filePath, string status)
{
    try
    {
        using (var connection = new DatabaseHelper().GetConnection())
        {
            connection.Open();

            // Check if a record with the same PatientID exists
            string selectQuery = "SELECT image_count FROM dicom_metadata WHERE patient_id = @PatientID";
            int currentImageCount = 0;

            using (var selectCommand = new SQLiteCommand(selectQuery, connection))
            {
                selectCommand.Parameters.AddWithValue("@PatientID", GetTagValue(dicomFile, DicomTag.PatientID));
                using (var reader = selectCommand.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        currentImageCount = reader.GetInt32(0); // Get the current image count
                    }
                }
            }

            if (currentImageCount > 0)
            {
                // Update the existing record's image count and other metadata
                string updateQuery = @"
                    UPDATE dicom_metadata
                    SET patient_name = @PatientName,
                        patient_id = @PatientID,
                        gender = @Gender,
                        age = @Age,
                        series_instance_uid = @SeriesInstanceUID,
                        sop_instance_uid = @SOPInstanceUID,
                        study_date = @StudyDate,
                        study_time = @StudyTime,
                        modality = @Modality,
                        manufacturer = @Manufacturer,
                        institution_name = @InstitutionName,
                        status = @Status,
                        file_path = @FilePath,
                        received_at = CURRENT_TIMESTAMP,
                        image_count = image_count + 1
                    WHERE patient_id = @PatientID";

                using (var updateCommand = new SQLiteCommand(updateQuery, connection))
                {
                    updateCommand.Parameters.AddWithValue("@PatientName", GetTagValue(dicomFile, DicomTag.PatientName));
                    updateCommand.Parameters.AddWithValue("@PatientID", GetTagValue(dicomFile, DicomTag.PatientID));
                    updateCommand.Parameters.AddWithValue("@Gender", GetTagValue(dicomFile, DicomTag.PatientSex));
                    updateCommand.Parameters.AddWithValue("@Age", GetTagValue(dicomFile, DicomTag.PatientAge));
                    updateCommand.Parameters.AddWithValue("@SeriesInstanceUID", GetTagValue(dicomFile, DicomTag.SeriesInstanceUID));
                    updateCommand.Parameters.AddWithValue("@SOPInstanceUID", GetTagValue(dicomFile, DicomTag.SOPInstanceUID));
                    updateCommand.Parameters.AddWithValue("@StudyDate", GetTagValue(dicomFile, DicomTag.StudyDate));
                    updateCommand.Parameters.AddWithValue("@StudyTime", GetTagValue(dicomFile, DicomTag.StudyTime));
                    updateCommand.Parameters.AddWithValue("@Modality", GetTagValue(dicomFile, DicomTag.Modality));
                    updateCommand.Parameters.AddWithValue("@Manufacturer", GetTagValue(dicomFile, DicomTag.Manufacturer));
                    updateCommand.Parameters.AddWithValue("@InstitutionName", GetTagValue(dicomFile, DicomTag.InstitutionName));
                    updateCommand.Parameters.AddWithValue("@Status", status);
                    updateCommand.Parameters.AddWithValue("@FilePath", filePath);
                    updateCommand.Parameters.AddWithValue("@PatientID", GetTagValue(dicomFile, DicomTag.PatientID));
                    updateCommand.ExecuteNonQuery();
                }
            }
            else
            {
                // Insert a new record
                string insertQuery = @"
                    INSERT INTO dicom_metadata (
                        patient_name, patient_id, gender, age, study_instance_uid, series_instance_uid,
                        sop_instance_uid, study_date, study_time, modality, manufacturer, institution_name,
                        status, file_path, image_count, received_at
                    ) VALUES (
                        @PatientName, @PatientID, @Gender, @Age, @StudyInstanceUID, @SeriesInstanceUID,
                        @SOPInstanceUID, @StudyDate, @StudyTime, @Modality, @Manufacturer, @InstitutionName,
                        @Status, @FilePath, 1, CURRENT_TIMESTAMP
                    )";

                using (var insertCommand = new SQLiteCommand(insertQuery, connection))
                {
                    insertCommand.Parameters.AddWithValue("@PatientName", GetTagValue(dicomFile, DicomTag.PatientName));
                    insertCommand.Parameters.AddWithValue("@PatientID", GetTagValue(dicomFile, DicomTag.PatientID));
                    insertCommand.Parameters.AddWithValue("@Gender", GetTagValue(dicomFile, DicomTag.PatientSex));
                    insertCommand.Parameters.AddWithValue("@Age", GetTagValue(dicomFile, DicomTag.PatientAge));
                    insertCommand.Parameters.AddWithValue("@StudyInstanceUID", GetTagValue(dicomFile, DicomTag.StudyInstanceUID));
                    insertCommand.Parameters.AddWithValue("@SeriesInstanceUID", GetTagValue(dicomFile, DicomTag.SeriesInstanceUID));
                    insertCommand.Parameters.AddWithValue("@SOPInstanceUID", GetTagValue(dicomFile, DicomTag.SOPInstanceUID));
                    insertCommand.Parameters.AddWithValue("@StudyDate", GetTagValue(dicomFile, DicomTag.StudyDate));
                    insertCommand.Parameters.AddWithValue("@StudyTime", GetTagValue(dicomFile, DicomTag.StudyTime));
                    insertCommand.Parameters.AddWithValue("@Modality", GetTagValue(dicomFile, DicomTag.Modality));
                    insertCommand.Parameters.AddWithValue("@Manufacturer", GetTagValue(dicomFile, DicomTag.Manufacturer));
                    insertCommand.Parameters.AddWithValue("@InstitutionName", GetTagValue(dicomFile, DicomTag.InstitutionName));
                    insertCommand.Parameters.AddWithValue("@Status", status);
                    insertCommand.Parameters.AddWithValue("@FilePath", filePath);
                    insertCommand.ExecuteNonQuery();
                }
            }
        }
    }
    catch (Exception ex)
    {
        Application.Current.Dispatcher.Invoke(() => MessageBox.Show($"Failed to save DICOM metadata: {ex.Message}"));
    }
}




        private void UpdateStatusInDatabase(string filePath, string status)
        {
            using (var connection = new DatabaseHelper().GetConnection())
            {
                connection.Open();
                string query = "UPDATE dicom_metadata SET status = @Status WHERE file_path = @FilePath";

                using (var command = new SQLiteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Status", status);
                    command.Parameters.AddWithValue("@FilePath", filePath);
                    command.ExecuteNonQuery();
                }
            }
        }

        private (int branchId, int clientId) GetAuthDetails()
        {
            try
            {
                using (var connection = new DatabaseHelper().GetConnection())
                {
                    connection.Open();
                    string query = "SELECT branch_id, client_id FROM auth WHERE id = 1";

                    using (var command = new SQLiteCommand(query, connection))
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            int branchId = reader.GetInt32(reader.GetOrdinal("branch_id"));
                            int clientId = reader.GetInt32(reader.GetOrdinal("client_id"));
                            return (branchId, clientId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => MessageBox.Show($"Failed to fetch auth details: {ex.Message}"));
            }
            return (0, 0); // Defaults to 0 if fetching fails
        }

        private string GetTagValue(DicomFile dicomFile, DicomTag tag)
        {
            return dicomFile.Dataset.Contains(tag) ? dicomFile.Dataset.GetSingleValueOrDefault<string>(tag, "N/A") : "N/A";
        }
       private void CompressDicomFile(string inputFilePath, string outputFilePath)
{
    // Check if the input file exists
    if (!File.Exists(inputFilePath))
    {
        throw new FileNotFoundException("Input DICOM file not found.", inputFilePath);
    }

    // List of compression syntaxes, ordered from highest to lowest compression
    var compressionSyntaxes = new[]
    {
        DicomTransferSyntax.JPEG2000Lossless,    // High compression, lossless
        DicomTransferSyntax.JPEGLSLossless,     // Moderate compression, lossless
        DicomTransferSyntax.JPEGProcess14SV1,   // Lossless JPEG
        DicomTransferSyntax.RLELossless,        // Run-Length Encoding (less efficient but still lossless)
        DicomTransferSyntax.JPEG2000Lossy,          // High compression, lossy
        DicomTransferSyntax.JPEGLSNearLossless // Low compression, near-lossless
    };

    try
    {
        // Load the DICOM file
        var dicomFile = DicomFile.Open(inputFilePath);
        var currentSyntax = dicomFile.Dataset.InternalTransferSyntax;
        Log($"Current Transfer Syntax: {currentSyntax.UID} - {currentSyntax}");

        // Attempt each compression syntax
        foreach (var syntax in compressionSyntaxes)
        {
            try
            {
                Log($"Attempting compression with syntax: {syntax.UID} - {syntax}");
                if (!syntax.IsEncapsulated)
                {
                    Log($"Skipping syntax {syntax} as it is not encapsulated.");
                    continue; // Skip non-encapsulated syntaxes
                }

                var transcoder = new DicomTranscoder(currentSyntax, syntax);
                var compressedFile = transcoder.Transcode(dicomFile);

                // Save the compressed file
                compressedFile.Save(outputFilePath);
                Log($"File successfully compressed to {syntax.UID} - {syntax}. Output saved to: {outputFilePath}");
                return; // Exit the method after successful compression
            }
            catch (Exception ex)
            {
                Log($"Failed to compress using {syntax}: {ex.Message}");
                // Continue to the next syntax
            }
        }

        // If all compression syntaxes fail, fallback to copying the input file
        Log("All compression attempts failed. Copying the input file to the output path.");
        File.Copy(inputFilePath, outputFilePath, overwrite: true);
    }
    catch (Exception ex)
    {
        Log($"Error during compression: {ex.Message}");
        // Ensure the input file is copied to the output path as a fallback
        File.Copy(inputFilePath, outputFilePath, overwrite: true);
        Log($"Input file copied to output path as fallback: {outputFilePath}");
    }
}





    
private void ConvertToRequiredTransferSyntax(string inputFilePath, string outputFilePath)
{
    // Define the desired transfer syntax (e.g., JPEG 2000 Lossless)
    var desiredTransferSyntax = DicomTransferSyntax.JPEG2000Lossless;

    try
    {
        // Load the DICOM file
        var dicomFile = DicomFile.Open(inputFilePath);

        // Get the current transfer syntax
        var currentTransferSyntax = dicomFile.Dataset.InternalTransferSyntax;

        // Log file size before processing
        FileInfo inputFileInfo = new FileInfo(inputFilePath);
        Log($"Input File Size: {inputFileInfo.Length / (1024.0 * 1024.0):F2} MB");
        Log($"Current Transfer Syntax: {currentTransferSyntax}");

        if (currentTransferSyntax == desiredTransferSyntax)
        {
            Log("Transfer syntax matches the desired format. No conversion needed.");
            File.Copy(inputFilePath, outputFilePath, overwrite: true);
        }
        else
        {
            try
            {
                // Convert the file to the desired transfer syntax
                var transcoder = new DicomTranscoder(currentTransferSyntax, desiredTransferSyntax);
                var transcodedFile = transcoder.Transcode(dicomFile);

                // Save the transcoded file
                transcodedFile.Save(outputFilePath);
                FileInfo outputFileInfo = new FileInfo(outputFilePath);
                Log($"File successfully converted to {desiredTransferSyntax.UID} - {desiredTransferSyntax}.");
                Log($"Output File Size: {outputFileInfo.Length / (1024.0 * 1024.0):F2} MB");
            }
            catch (NotSupportedException ex)
            {
                // Log and fallback to copying the input file
                Log($"Compression not supported for {desiredTransferSyntax}: {ex.Message}");
                Log("Falling back to copying the input file.");
                File.Copy(inputFilePath, outputFilePath, overwrite: true);
            }
        }
    }
    catch (Exception ex)
    {
        Log($"Error during transfer syntax conversion: {ex.Message}");
        // Ensure the input file is copied to the output path as a fallback
        File.Copy(inputFilePath, outputFilePath, overwrite: true);
        Log($"Input file copied to output path as fallback: {outputFilePath}");
    }
}









    }
}
