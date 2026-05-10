# Table Continuation Detection Module

Tài liệu kỹ thuật cho module phát hiện hai bảng có phải là sự tiếp nối (continuation) của nhau hay không.

---

## 1. Vấn đề

Khi OCR một tài liệu PDF nhiều trang, các bảng bị cắt ngang trang — một bảng lớn bị split thành nhiều bảng nhỏ. Cần xác định xem hai bảng kề nhau có phải là continuation của cùng một bảng gốc để gộp lại thành một chunk.

**Input**: Markdown (Markdig parsed) với hỗn hợp `Table` (Markdig native) và `<table>` HTML.

---

## 2. Kiến trúc

```
Helper/MarkdownServiceHelper.cs         ← Static helpers, không dependency
  ├── TableContinuationInfo record
  ├── ExtractTableInfo()
  ├── CalculateSimilarity()
  ├── DiceBigram / CellSimilarity / CompareHeaders / ColumnSimilarity
  └── StripNestedTables()               ← Xử lý HTML table lồng nhau

Controllers/TestTableContinuationController.cs
  ├── Run(documentId)                    ← Endpoint test
  ├── CallAiFallbackAsync()              ← AI fallback
  └── Multi-pair voting loop (μ ± σ threshold)
```

---

## 3. Luồng xử lý

```
source (OCR markdown)
  ↓ Parse blocks bằng Markdig (pipeline = UseAdvancedExtensions)
  ↓ Lọc table blocks (native Table hoặc HtmlBlock <table>)
  ↓ Với mỗi table block:
      ExtractTableInfo → TableContinuationInfo
  ↓ For each candidate table:
      Lấy segment hiện tại
      Filter headerRefs = segment.Where(t.HasHeader == true)
      if headerRefs rỗng → AI fallback
      else:
          scores = headerRefs.Select(h → CalculateSimilarity(h, candidate))
          μ = Average(scores)
          σ = StdDev(scores)
          if μ - σ ≥ 0.70 → Heuristic: Continuation (merge)
          elif μ + σ ≤ 0.25 → Heuristic: Not Continuation (split)
          else → AI fallback
  ↓ Output: chunks (nhóm các table đã merge)
```

---

## 4. Dữ liệu cốt lõi: TableContinuationInfo

```csharp
public sealed record TableContinuationInfo(
    int ColumnCount,         // Số cột của bảng
    List<string>? HeaderCells, // Nội dung các header cell (null nếu ko có header)
    bool HasHeader,          // Bảng có header row không
    int RowCount             // Số data rows (không tính header)
);
```

Trích xuất từ 2 loại block:

### Native Markdig Table

```csharp
table.OfType<TableRow>()
  ├── row.IsHeader == true  → HeaderCells = cells.Select(c → source[c.Span])
  └── row.IsHeader == false → Data rows count
ColumnCount từ: header count > ColumnDefinitions.Count > max cell count
```

### HTML `<table>` trong HtmlBlock

```csharp
StripNestedTables(html)     // Loại bỏ bảng con lồng bên trong <td>
  → Regex parse <tr>, <th>, <td> ở outer table
```

---

## 5. Thuật toán so sánh

### 5.1 Dice Bigram Coefficient

So sánh hai chuỗi ký tự, dùng cho header cell text.

**Nguồn**: Adamson & Boreham (1974), Information Retrieval.

```
bigrams(s) = { s[i..i+2] | i = 0 .. len(s)-2 }

Dice(a, b) = 2 × |bigrams(a) ∩ bigrams(b)| / (|bigrams(a)| + |bigrams(b)|)
```

**Đặc điểm**:
- O(n) — nhanh
- Chịu được OCR noise (thừa/thiếu 1-2 ký tự)
- Không cần training, không dependency ngoài

**Ví dụ**:

| a | b | Dice | Ý nghĩa |
|---|---|---|---|
| `"Ngày"` | `"Ngày"` | 1.0 | Giống hệt |
| `"Ngày"` | `"Ngày "` | 6/7 ≈ 0.857 | Thừa space OCR |
| `"Mô tả"` | `"Mô t ả"` | ~0.86 | Sai space do OCR |
| `"Ngày"` | `"Số tiền"` | 0.0 | Khác hẳn |

---

### 5.2 CellSimilarity

So sánh nội dung hai header cell, trả về [0.0, 1.0].

```
CellSimilarity(c1, c2):
  n1 = Trim(Lower(c1))
  n2 = Trim(Lower(c2))

  n1 == n2                     → 1.0    // Khớp tuyệt đối
  Contains(n1, n2)             → 0.8    // Một cái chứa cái kia
  Contains(n2, n1)             → 0.8    // VD: "Tên" vs "Tên KH"
  Dice(n1, n2) >= 0.6          → 0.6    // OCR noise nhẹ
  else                         → 0.0    // Khác hẳn
```

Ngưỡng Dice ≥ 0.6 là empirical — OCR noise 1-2 ký tự thường cho Dice ≥ 0.6.

---

### 5.3 CompareHeaders

So sánh list header của hai bảng.

Hai bảng có thể khác số cột (merged cell, artifact):

```
CompareHeaders(h1, h2):
  minCols = Min(len(h1), len(h2))
  maxCols = Max(len(h1), len(h2))

  pairwiseAvg = Average(
    CellSimilarity(h1[i], h2[i])    // i = 0 .. minCols-1
  )
  colPenalty = minCols / maxCols

  result = pairwiseAvg × colPenalty
```

**Ví dụ**:

| h1 | h2 | pairwiseAvg | penalty | Kết quả |
|---|---|---|---|---|
| `[A,B,C]` | `[A,B,C]` | 1.0 | 1.0 | 1.0 |
| `[A,B,C]` | `[A,B,C,D]` | 1.0 | 0.75 | 0.75 |
| `[A,B,C]` | `[A,X,C]` | (1+0+1)/3=0.67 | 1.0 | 0.67 |

---

### 5.4 ColumnSimilarity

So sánh số cột:

```
ColumnSimilarity(c1, c2):
  c1 == c2          → 1.0
  |c1 - c2| == 1    → 0.4    // Sai lệch 1 cột (merged cell)
  else              → 0.0    // Khác hẳn
```

---

### 5.5 CalculateSimilarity — tổng hợp cho 1 cặp

Kết hợp header similarity + column similarity, routing theo HasHeader.

| T1.HasHeader | T2.HasHeader | Công thức | Giải thích |
|---|---|---|---|
| true | true | `HeaderSim×0.8 + ColSim×0.2` | Header là tín hiệu mạnh nhất |
| true | false | `0.7 + ColSim×0.2` | T2 lược header — pattern continuation phổ biến |
| false | true | `0.1 + ColSim×0.2` | Không hợp lý cho continuation |
| false | false | `0.5 + ColSim×0.4` | Grey zone cố ý, thường rơi vào AI |

**Tại sao trọng số 80% / 20%?**
- Header match là tín hiệu **quyết định** — hai bảng khác header gần như chắc chắn không phải continuation
- Column chỉ là tín hiệu **hỗ trợ** — cùng cột chưa chắc cùng bảng

---

## 6. Multi-pair Voting

Khi segment đã gộp nhiều table, ta so sánh candidate với **tất cả các table có header** trong segment.

```
headerRefs = segment.Where(t.HasHeader == true)

// KHÔNG lấy table không header
// (lý do: table không header là continuation của table có header,
//  so sánh với nó chỉ gây nhiễu)

S = { CalculateSimilarity(t_header, candidate) | t ∈ headerRefs }

minScore = Min(S)
maxScore = Max(S)
```

**Tại sao dùng min/max thay vì mean ± stddev?**
- Các pair scores không cùng population (mỗi pair dùng công thức `CalculateSimilarity` khác nhau do routing HasHeader) → variance không có ý nghĩa thống kê
- N (header refs) thường rất nhỏ (1-3) → σ unstable
- min/max đọc trực tiếp từ data, không cần giả định phân bố

---

## 7. Decision Thresholds

| Điều kiện | Quyết định | Method |
|---|---|---|
| `minScore ≥ 0.70` | **Continuation** | Heuristic |
| `maxScore ≤ 0.25` | **Not Continuation** | Heuristic |
| Còn lại | **AI Fallback** | Gọi LLM |

**Tại sao dùng min/max thay vì trung bình?**

| Tình huống | scores | mean | stddev | μ-σ | min | Kết luận đúng |
|---|---|---|---|---|---|---|
| Đồng thuận cao | [0.96, 0.97] | 0.965 | 0.005 | 0.960 | 0.96 | Continuation (min ≥ 0.70) |
| Bất đồng nhẹ | [0.71, 0.94] | 0.825 | 0.115 | 0.710 | 0.71 | Continuation (min ≥ 0.70) |
| Bất đồng mạnh | [0.50, 0.95] | 0.725 | 0.225 | 0.500 | 0.50 | Grey zone → AI |

**Lịch sử threshold**:
- Ban đầu dùng μ ± σ (threshold 0.75)
- Sau đổi sang min/max (threshold 0.70) — trực quan hơn, không cần giả định thống kê

---

## 8. AI Fallback

Khi heuristic không kết luận được, gọi LLM với prompt hiện tại:

```
Prompt: data/prompts/promptChoiceV2.md

{0} = Context (từ first table segment đến candidate)
{1} = Candidate table content

LLM trả lời: "Yes" hoặc "No"
```

Sử dụng `ChatChoiceAsync` với `SubmitChoice` tool, 3 lần retry.

**Chi phí**: ~44 giây / call với model selection (server Nvidia NIM).

---

## 9. Xử lý HTML table lồng nhau

### Vấn đề

Nhiều tài liệu có bảng lồng nhau:
```html
<table>                      <!-- outer table, không header -->
  <tr><td>
    <table><thead>           <!-- nested table, CÓ header -->
      <tr><th>Nếu</th><th>Thì</th></tr>
    </table>
  </td></tr>
</table>
```

Regex `\b<th\b` match cả thẻ `<th>` trong bảng con → sai `HasHeader = true`.

### Giải pháp: StripNestedTables

```csharp
StripNestedTables(html):
  depth = 0
  for each character:
    <table → depth++
      if depth==1: keep <table>
    </table → depth--
      if depth==0: keep </table>
    if depth==1: keep character
```

Depth counter giữ đúng nesting level — chỉ parse cấu trúc outer table.

---

## 10. JSON Output — Encoding

File result lưu và API response dùng:

```csharp
JsonSerializerOptions {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
}
```

`UnsafeRelaxedJsonEscaping`:
- Không escape ký tự Unicode (tiếng Việt) — mặc định `\u00EA`
- Không escape HTML-sensitive chars (`< > " &`) — mặc định `\u003C`

---

## 11. File tham chiếu

| File | Mục đích |
|---|---|
| `Helper/MarkdownServiceHelper.cs` | Static helpers: ExtractTableInfo, CalculateSimilarity, DiceBigram, StripNestedTables, GetTableBlocks, GetScoreRange, HeuristicDecisionByRange |
| `Controllers/TestTableContinuationController.cs` | Test endpoint + multi-pair voting + AI fallback |
| `data/prompts/promptChoiceV2.md` | Prompt cho AI fallback |
| `https/TableContinuation.http` | HTTP request file để test |
| `Logs/table-test-*.json` | Kết quả test tự động lưu |

---

## 12. Tuning Guide

Nếu cần điều chỉnh độ nhạy:

| Tham số | Giá trị hiện tại | Ảnh hưởng |
|---|---|---|
| `minScore threshold` | 0.70 | Cao hơn → dễ continuation hơn (ít split), an toàn hơn |
| `maxScore threshold` | 0.25 | Thấp hơn → dễ NOT continuation (nhiều split), nhiều AI call hơn |
| `CellSimilarity Dice cutoff` | 0.6 | Cao hơn → khó match hơn do OCR noise |
| `Header / Column weights` | 0.8 / 0.2 | Tăng header weight → ưu tiên header match |
| `Base score T1+T2-` | 0.7 | Cao hơn → dễ merge khi T2 không header |
