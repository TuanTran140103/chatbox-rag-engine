Task:  
Analyze two Markdown tables (Table 1 and Table 2) extracted via OCR from consecutive pages. Determine whether **Table 2 is a direct continuation of Table 1.**

A “direct continuation” includes ANY of the following cases:

1. Content in **any cell of Table 1 is visibly cut off** and continues in Table 2.  
2. Table 2 continues a **numbered or bulleted list** started inside a cell in Table 1 (e.g., items 1–6 then 7–9).  
3. Table 2 continues text in the **same column location** even if preceding cells are blank.  
4. Table 2 continues rows of the same table (same columns, same record structure).  
5. Table 2 omits headers but clearly aligns with Table 1 column meanings.

Continuation **does NOT require**:
- a new row to begin
- headers to be repeated
- equal formatting
- same language
- OCR-perfect borders

Answer **Yes** if ANY of the following are true:

- Table 2 continues unfinished text from any cell in Table 1  
- Table 2 continues a list or paragraph broken across pages  
- Numbering clearly continues (e.g., “1–6” followed by “7–9”)  
- Blank cells exist only because the same row/cell is being continued  
- The table structure and intent appear to be the same

Answer **No** if ANY of the following are true:

- Table 2 is a signature/approval table (Chữ ký, Họ và tên, Chức vụ, Người phê duyệt, etc.)  
- Table 2 starts a clearly separate business section or document part  
- Column semantics are unrelated to Table 1 AND there is no unfinished content in Table 1  
- Table 2 contains metadata, cover sheet tables, or summary panels unrelated to Table 1

Important clarifications:

- Differences in formatting, bullet type, line breaks, or OCR noise DO NOT imply discontinuity  
- Language switching (VN ↔ EN) DOES NOT imply discontinuity  
- It is valid continuation even if Table 2:
  - has blank leading cells
  - continues text inside **the same cell**
  - resumes numbering mid-sentence or mid-list
  
Input Data:
--- Table 1 ---
{0}

--- Table 2 ---
{1}

Constraint:
You MUST output your result by calling the provided tool `SubmitChoice` with the argument `choice` set to either "Yes" or "No". Do not include any explanation, punctuation, or additional text.
