using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IControlReporter.Models
{
    [Table("analogevents")] // 👈 如果你的資料表小寫，請在這裡指定實際的資料表名稱
    public class analogevents
    {
        [Key]
        [Column("idEvent")]
        public long IdEvent { get; set; } // BIGINT -> long (Primary Key, Auto Increment)

        [Column("seqNumber")]
        public long SeqNumber { get; set; } // BIGINT -> long

        [Column("eventTime")]
        public DateTime EventTime { get; set; } // DATETIME -> DateTime

        [Column("eventTimeQ")]
        public sbyte EventTimeQ { get; set; } // TINYINT -> sbyte (微控系統常用作狀態碼)

        [Column("eventTimeLocal")]
        public sbyte EventTimeLocal { get; set; } // TINYINT -> sbyte

        [Column("quality")]
        [StringLength(25)]
        public string? Quality { get; set; } // VARCHAR(25) 允許 NULL -> string?

        [Column("value")]
        public float? Value { get; set; } // FLOAT 允許 NULL -> float?

        [Column("COT")]
        public int? Cot { get; set; } // INT 允許 NULL -> int?

        [Column("points_idPoint")]
        public int PointsIdPoint { get; set; } // INT -> int (外鍵欄位)

        [Column("DynamicTexts_idDynamicTexts")]
        public int? DynamicTextsIdDynamicText { get; set; } // INT 允許 NULL -> int? (外鍵欄位)

        [Column("exec_result")]
        public int ExecResult { get; set; } = 1; // INT 不允許 NULL，預設值為 1
    }
}