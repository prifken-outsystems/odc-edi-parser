using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OutSystems.ExternalLibraries.SDK;

namespace EdiParserLibrary
{
    // ── Output structures ─────────────────────────────────────────────────────

    [OSStructure(Description = "A single line item extracted from an EDI 850 PO document.")]
    public struct EdiLineItem
    {
        [OSStructureField(Description = "Line sequence number from PO1 segment.")]
        public int LineNumber { get; set; }

        [OSStructureField(Description = "Ordered quantity.")]
        public double Quantity { get; set; }

        [OSStructureField(Description = "Unit of measure code e.g. EA, CS, LB.")]
        public string UnitOfMeasure { get; set; }

        [OSStructureField(Description = "Unit price per item.")]
        public double UnitPrice { get; set; }

        [OSStructureField(Description = "Line total (Quantity x UnitPrice).")]
        public double LineTotal { get; set; }

        [OSStructureField(Description = "Vendor's part number (VN qualifier in PO1).")]
        public string VendorPartNumber { get; set; }

        [OSStructureField(Description = "Human-readable description from PID segment, if present.")]
        public string Description { get; set; }

        [OSStructureField(Description = "Extraction confidence: high / medium / low.")]
        public string Confidence { get; set; }
    }

    // ── Interface ─────────────────────────────────────────────────────────────

    [OSInterface(
        Name = "EdiParser",
        Description = "Parses EDI 850 Purchase Order documents transmitted by vendors. " +
                      "Extracts vendor identity, PO metadata, line items, amounts, and dates. " +
                      "Returns structured JSON with per-field confidence scores. " +
                      "Replaces manual data entry from vendor EDI attachments in the PO intake workflow."
    )]
    public interface IEdiParser
    {
        [OSAction(
            Description = "Parse a complete EDI 850 Purchase Order document. " +
                          "Returns a JSON object containing PO number, vendor name, currency, dates, " +
                          "line items, total amount, and an overall confidence score."
        )]
        string ParsePurchaseOrder(
            [OSParameter(Description = "Raw EDI file content as bytes (ASCII-encoded).")] byte[] ediBytes
        );

        [OSAction(
            Description = "Extract only the PO1 line items from an EDI 850 document. " +
                          "Returns a JSON array of EdiLineItem objects. " +
                          "Faster than ParsePurchaseOrder when only line-item data is needed."
        )]
        string ExtractLineItems(
            [OSParameter(Description = "Raw EDI file content as bytes.")] byte[] ediBytes
        );

        [OSAction(
            Description = "Validate the EDI envelope structure. " +
                          "Checks that ISA/IEA, GS/GE, and ST/SE segment pairs are balanced. " +
                          "Returns true if the envelope is structurally valid."
        )]
        bool ValidateEnvelope(
            [OSParameter(Description = "Raw EDI file content as bytes.")] byte[] ediBytes
        );

        [OSAction(
            Description = "Returns build version metadata. Used internally to ensure each CI/CD build " +
                          "produces a unique library revision in ODC."
        )]
        string GetBuildVersion();
    }

    // ── Implementation ────────────────────────────────────────────────────────

    public class EdiParser : IEdiParser
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // ── EDI segment codes ────────────────────────────────────────────────
        private const string SEG_ISA = "ISA";   // Interchange Control Header — defines delimiters
        private const string SEG_GS  = "GS";    // Functional Group Header
        private const string SEG_ST  = "ST";    // Transaction Set Header
        private const string SEG_BEG = "BEG";   // Beginning Segment — PO number, type, date
        private const string SEG_CUR = "CUR";   // Currency
        private const string SEG_REF = "REF";   // Reference Identification
        private const string SEG_DTM = "DTM";   // Date / Time Reference
        private const string SEG_N1  = "N1";    // Name — vendor or buyer
        private const string SEG_N3  = "N3";    // Street Address
        private const string SEG_N4  = "N4";    // City / State / Zip
        private const string SEG_PO1 = "PO1";   // Baseline Item Data — one segment per line item
        private const string SEG_PID = "PID";   // Product / Item Description
        private const string SEG_AMT = "AMT";   // Monetary Amount (total)
        private const string SEG_CTT = "CTT";   // Transaction Totals

        // N1 entity qualifier codes
        private const string N1_VENDOR   = "SE";  // Selling Party (vendor)
        private const string N1_VENDOR_2 = "VN";  // Vendor (alternate qualifier)
        private const string N1_BUYER    = "BY";  // Buying Party

        // DTM date qualifier codes
        private const string DTM_DELIVERY = "002"; // Required Delivery Date
        private const string DTM_SHIP     = "010"; // Requested Ship Date
        private const string DTM_PO_DATE  = "004"; // Purchase Order Date

        // ── Public actions ───────────────────────────────────────────────────

        public string ParsePurchaseOrder(byte[] ediBytes)
        {
            var log = new StringBuilder();
            var start = DateTime.UtcNow;
            log.AppendLine($"=== EdiParser.ParsePurchaseOrder ===");
            log.AppendLine($"Started: {start:yyyy-MM-dd HH:mm:ss.fff} UTC");

            try
            {
                log.AppendLine($"Input bytes: {ediBytes?.Length ?? 0}");

                if (ediBytes == null || ediBytes.Length == 0)
                    throw new ArgumentException("ediBytes is null or empty");

                // Peek at raw content for diagnostics
                string preview = Encoding.ASCII.GetString(ediBytes, 0, Math.Min(80, ediBytes.Length));
                log.AppendLine($"Content preview: {preview}");

                log.AppendLine("[1] Parsing envelope...");
                var (segments, elementSep) = ParseEnvelope(ediBytes);
                log.AppendLine($"    Element separator: '{elementSep}'");
                log.AppendLine($"    Segments found: {segments.Count}");
                log.AppendLine($"    Segment codes: {string.Join(", ", segments.Select(s => s[0].Trim()).Distinct())}");

                log.AppendLine("[2] Extracting PO data...");
                var result = ExtractPurchaseOrderData(segments);

                log.AppendLine($"[3] Extraction complete");
                log.AppendLine($"    Duration: {(DateTime.UtcNow - start).TotalMilliseconds:F0}ms");

                result["executionLog"] = log.ToString();
                return JsonSerializer.Serialize(result, JsonOpts);
            }
            catch (Exception ex)
            {
                log.AppendLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
                log.AppendLine($"Stack: {ex.StackTrace}");
                return JsonSerializer.Serialize(new
                {
                    error = true,
                    message = ex.Message,
                    overallConfidence = "low",
                    executionLog = log.ToString()
                }, JsonOpts);
            }
        }

        public string ExtractLineItems(byte[] ediBytes)
        {
            var log = new StringBuilder();
            log.AppendLine($"=== EdiParser.ExtractLineItems ===");
            log.AppendLine($"Started: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");

            try
            {
                log.AppendLine($"Input bytes: {ediBytes?.Length ?? 0}");

                if (ediBytes == null || ediBytes.Length == 0)
                    throw new ArgumentException("ediBytes is null or empty");

                var (segments, _) = ParseEnvelope(ediBytes);
                log.AppendLine($"Segments found: {segments.Count}");

                var items = ExtractLineItemData(segments);
                log.AppendLine($"Line items extracted: {items.Count}");

                // Return items wrapped with log for diagnostics
                return JsonSerializer.Serialize(new
                {
                    items,
                    lineItemCount = items.Count,
                    executionLog = log.ToString()
                }, JsonOpts);
            }
            catch (Exception ex)
            {
                log.AppendLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    error = true,
                    message = ex.Message,
                    items = Array.Empty<EdiLineItem>(),
                    executionLog = log.ToString()
                }, JsonOpts);
            }
        }

        public string GetBuildVersion()
        {
            // This line is replaced by CI/CD on every build to force a unique modelDigest in ODC.
            // DO NOT remove or modify the placeholder string.
            var buildMetadata = "BUILD_METADATA_PLACEHOLDER";
            return $"EdiParser | {buildMetadata}";
        }

        public bool ValidateEnvelope(byte[] ediBytes)
        {
            try
            {
                var (segments, _) = ParseEnvelope(ediBytes);

                int isaCount = 0, ieaCount = 0;
                int gsCount  = 0, geCount  = 0;
                int stCount  = 0, seCount  = 0;

                foreach (var seg in segments)
                {
                    switch (seg[0].Trim())
                    {
                        case "ISA": isaCount++; break;
                        case "IEA": ieaCount++; break;
                        case "GS":  gsCount++;  break;
                        case "GE":  geCount++;  break;
                        case "ST":  stCount++;  break;
                        case "SE":  seCount++;  break;
                    }
                }

                return isaCount == ieaCount
                    && gsCount  == geCount
                    && stCount  == seCount
                    && isaCount >= 1;
            }
            catch
            {
                return false;
            }
        }

        // ── Envelope parser ──────────────────────────────────────────────────

        // The ISA segment is always 106 characters wide and encodes its own delimiters.
        // Element separator  = character at index 3 (right after "ISA")
        // Segment terminator = character at index 105 (immediately after ISA[16])
        private static (List<string[]> segments, char elementSep) ParseEnvelope(byte[] ediBytes)
        {
            string raw = Encoding.ASCII.GetString(ediBytes).Trim();

            if (!raw.StartsWith("ISA", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "EDI document does not begin with an ISA segment. " +
                    "Verify the file is a valid X12 interchange."
                );

            char elementSep  = raw[3];    // e.g. '*'
            char segmentTerm = raw[105];  // e.g. '~'

            var rawSegments = raw.Split(segmentTerm, StringSplitOptions.RemoveEmptyEntries);

            var segments = rawSegments
                .Select(s => s.Trim().Split(elementSep))
                .Where(s => s.Length > 0 && !string.IsNullOrWhiteSpace(s[0]))
                .ToList();

            return (segments, elementSep);
        }

        // ── PO extraction ────────────────────────────────────────────────────

        private static Dictionary<string, object?> ExtractPurchaseOrderData(List<string[]> segments)
        {
            var fields = new Dictionary<string, object?>();
            var lineItems = new List<EdiLineItem>();
            var pidDescriptions = new Dictionary<int, string>(); // lineNumber → description
            int lastLineNumber = 0;

            string? vendorName    = null;
            string? vendorId      = null;
            string? vendorAddress = null;
            string? vendorCity    = null;
            string? buyerName     = null;
            string? currentN1     = null;
            double  totalAmount   = 0;
            bool    amtFromSegment = false;

            foreach (var seg in segments)
            {
                string code = seg[0].Trim();

                switch (code)
                {
                    // ── PO header ────────────────────────────────────────────
                    case SEG_BEG:
                        // BEG * <purpose> * <type> * <PO number> * <release> * <date>
                        fields["poNumber"]        = Get(seg, 3);
                        fields["poType"]          = MapPoType(Get(seg, 2));
                        fields["poDate"]          = FormatDate(Get(seg, 5));
                        fields["poNumberConfidence"] = Get(seg, 3) != null ? "high" : "low";
                        break;

                    case SEG_CUR:
                        // CUR * <entity qualifier> * <currency code>
                        fields["currency"] = Get(seg, 2);
                        break;

                    case SEG_REF:
                        // REF * <qualifier> * <value>
                        switch (Get(seg, 1))
                        {
                            case "VN": fields["vendorRefNumber"] = Get(seg, 2); break;
                            case "CT": fields["contractNumber"]  = Get(seg, 2); break;
                            case "ZZ": fields["customRef"]       = Get(seg, 2); break;
                        }
                        break;

                    case SEG_DTM:
                        // DTM * <qualifier> * <YYYYMMDD>
                        switch (Get(seg, 1))
                        {
                            case DTM_DELIVERY: fields["requiredDeliveryDate"] = FormatDate(Get(seg, 2)); break;
                            case DTM_SHIP:     fields["requestedShipDate"]    = FormatDate(Get(seg, 2)); break;
                            case DTM_PO_DATE:  fields["purchaseOrderDate"]    = FormatDate(Get(seg, 2)); break;
                        }
                        break;

                    // ── Vendor / buyer identity ──────────────────────────────
                    case SEG_N1:
                        // N1 * <qualifier> * <name> * <id type> * <id value>
                        currentN1 = Get(seg, 1);
                        string? n1Name = Get(seg, 2);
                        string? n1Id   = Get(seg, 4);

                        if (currentN1 == N1_VENDOR || currentN1 == N1_VENDOR_2)
                        {
                            vendorName = n1Name;
                            vendorId   = n1Id;
                        }
                        else if (currentN1 == N1_BUYER)
                        {
                            buyerName = n1Name;
                        }
                        break;

                    case SEG_N3:
                        // N3 * <address line 1>
                        if (currentN1 == N1_VENDOR || currentN1 == N1_VENDOR_2)
                            vendorAddress = Get(seg, 1);
                        break;

                    case SEG_N4:
                        // N4 * <city> * <state> * <zip> * <country>
                        if (currentN1 == N1_VENDOR || currentN1 == N1_VENDOR_2)
                            vendorCity = $"{Get(seg, 1)}, {Get(seg, 2)} {Get(seg, 3)}".Trim(' ', ',');
                        break;

                    // ── Line items ───────────────────────────────────────────
                    case SEG_PO1:
                        var item = ParseLineItem(seg);
                        lastLineNumber = item.LineNumber;
                        lineItems.Add(item);
                        break;

                    case SEG_PID:
                        // PID * F * * <agency> * <code> * <description free-form>
                        string? desc = Get(seg, 5);
                        if (desc != null && lastLineNumber > 0)
                            pidDescriptions[lastLineNumber] = desc;
                        break;

                    // ── Totals ───────────────────────────────────────────────
                    case SEG_AMT:
                        // AMT * <qualifier> * <amount>
                        string? amtQual = Get(seg, 1);
                        if ((amtQual == "TT" || amtQual == "1") &&
                            double.TryParse(Get(seg, 2), out double amtVal))
                        {
                            totalAmount    = amtVal;
                            amtFromSegment = true;
                        }
                        break;

                    case SEG_CTT:
                        // CTT * <line item count> * <hash total>
                        if (int.TryParse(Get(seg, 1), out int cttCount))
                            fields["transactionLineCount"] = cttCount;
                        break;
                }
            }

            // Enrich line items with PID descriptions
            for (int i = 0; i < lineItems.Count; i++)
            {
                var li = lineItems[i];
                if (pidDescriptions.TryGetValue(li.LineNumber, out string? pidDesc))
                {
                    li.Description = pidDesc;
                    lineItems[i] = li;
                }
            }

            // If no AMT segment, calculate total from line items
            if (!amtFromSegment && lineItems.Count > 0)
            {
                totalAmount = lineItems.Sum(li => li.LineTotal);
                fields["totalAmountSource"] = "calculated";
            }
            else if (amtFromSegment)
            {
                fields["totalAmountSource"] = "AMT segment";
            }

            // Assemble vendor block
            fields["vendorName"]           = vendorName;
            fields["vendorId"]             = vendorId;
            fields["vendorAddress"]        = vendorAddress;
            fields["vendorCity"]           = vendorCity;
            fields["vendorConfidence"]     = vendorName != null ? "high" : "low";
            fields["buyerName"]            = buyerName;
            fields["totalAmount"]          = totalAmount > 0 ? (object)Math.Round(totalAmount, 2) : null;
            fields["lineItemCount"]        = lineItems.Count;
            fields["lineItems"]            = lineItems;
            fields["extractedAt"]          = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            fields["overallConfidence"]    = DeriveConfidence(fields, lineItems.Count);

            return fields;
        }

        // ── Line item extraction ─────────────────────────────────────────────

        private static List<EdiLineItem> ExtractLineItemData(List<string[]> segments)
        {
            var items = new List<EdiLineItem>();
            var pidDescriptions = new Dictionary<int, string>();
            int lastLineNumber = 0;

            foreach (var seg in segments)
            {
                string code = seg[0].Trim();
                if (code == SEG_PO1)
                {
                    var item = ParseLineItem(seg);
                    lastLineNumber = item.LineNumber;
                    items.Add(item);
                }
                else if (code == SEG_PID && lastLineNumber > 0)
                {
                    string? desc = Get(seg, 5);
                    if (desc != null) pidDescriptions[lastLineNumber] = desc;
                }
            }

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (pidDescriptions.TryGetValue(item.LineNumber, out string? d))
                {
                    item.Description = d;
                    items[i] = item;
                }
            }

            return items;
        }

        // ── PO1 segment parser ───────────────────────────────────────────────

        // PO1 structure:
        // PO1 * <line seq> * <qty> * <UOM> * <unit price> * <price qualifier>
        //     * <product qualifier 1> * <product id 1>
        //     * <product qualifier 2> * <product id 2> ...
        private static EdiLineItem ParseLineItem(string[] seg)
        {
            int.TryParse(Get(seg, 1), out int lineNum);
            double.TryParse(Get(seg, 2), out double qty);
            string uom = Get(seg, 3) ?? "EA";
            double.TryParse(Get(seg, 4), out double unitPrice);

            string? vendorPart = null;
            string? buyerPart  = null;

            // Product qualifier / value pairs start at element 6
            for (int i = 6; i + 1 < seg.Length; i += 2)
            {
                string qual = seg[i].Trim();
                string val  = seg[i + 1].Trim();
                if (qual == "VN") vendorPart = val;
                else if (qual == "BP") buyerPart = val;
            }

            return new EdiLineItem
            {
                LineNumber       = lineNum,
                Quantity         = qty,
                UnitOfMeasure    = uom,
                UnitPrice        = unitPrice,
                LineTotal        = Math.Round(qty * unitPrice, 2),
                VendorPartNumber = vendorPart ?? "",
                Description      = buyerPart != null ? $"Buyer PN: {buyerPart}" : "",
                Confidence       = qty > 0 && unitPrice > 0 ? "high" : "medium",
            };
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string? Get(string[] seg, int index)
        {
            if (index < 0 || index >= seg.Length) return null;
            string val = seg[index].Trim();
            return string.IsNullOrEmpty(val) ? null : val;
        }

        // EDI dates arrive as YYYYMMDD or YYMMDD — normalise to ISO 8601
        private static string? FormatDate(string? raw)
        {
            if (raw == null) return null;

            if (raw.Length == 8 &&
                DateTime.TryParseExact(raw, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d8))
                return d8.ToString("yyyy-MM-dd");

            if (raw.Length == 6 &&
                DateTime.TryParseExact(raw, "yyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d6))
                return d6.ToString("yyyy-MM-dd");

            return raw;  // Return as-is if unrecognised format
        }

        private static string? MapPoType(string? code) => code switch
        {
            "NE" => "New Order",
            "CF" => "Confirmation",
            "CN" => "Blanket Order",
            "KN" => "Agreement",
            "RO" => "Rush Order",
            "SA" => "Standing Order",
            _    => code,
        };

        private static string DeriveConfidence(Dictionary<string, object?> fields, int lineItemCount)
        {
            bool hasPoNumber = fields.GetValueOrDefault("poNumber") != null;
            bool hasVendor   = fields.GetValueOrDefault("vendorName") != null;
            bool hasItems    = lineItemCount > 0;
            bool hasAmount   = fields.GetValueOrDefault("totalAmount") != null;

            int score = (hasPoNumber ? 1 : 0) + (hasVendor ? 1 : 0)
                      + (hasItems   ? 1 : 0) + (hasAmount ? 1 : 0);

            return score >= 4 ? "high"
                 : score >= 2 ? "medium"
                 : "low";
        }
    }
}
