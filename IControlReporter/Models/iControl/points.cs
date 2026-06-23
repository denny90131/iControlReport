using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("points")] // 請自行替換成實際的資料表名稱
public class points
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("idPoint")]
    public int IdPoint { get; set; }

    [Required]
    [StringLength(255)]
    [Column("tag")]
    public string Tag { get; set; } = string.Empty;

    [StringLength(45)]
    [Column("format")]
    public string? Format { get; set; }

    [Column("style")]
    public int Style { get; set; }

    [StringLength(512)]
    [Column("T0")]
    public string? T0 { get; set; }

    [StringLength(512)]
    [Column("T1")]
    public string? T1 { get; set; }

    [StringLength(512)]
    [Column("T2")]
    public string? T2 { get; set; }

    [StringLength(512)]
    [Column("T3")]
    public string? T3 { get; set; }

    [StringLength(512)]
    [Column("T4")]
    public string? T4 { get; set; }

    [StringLength(512)]
    [Column("T5")]
    public string? T5 { get; set; }

    [StringLength(512)]
    [Column("T6")]
    public string? T6 { get; set; }

    [StringLength(512)]
    [Column("T7")]
    public string? T7 { get; set; }

    [StringLength(512)]
    [Column("T8")]
    public string? T8 { get; set; }

    [StringLength(512)]
    [Column("T9")]
    public string? T9 { get; set; }

    [Required]
    [Column("sources_idSources")]
    public int SourcesIdSources { get; set; }

    [Required]
    [Column("PointType_idPointType")]
    public int PointTypeIdPointType { get; set; }
}