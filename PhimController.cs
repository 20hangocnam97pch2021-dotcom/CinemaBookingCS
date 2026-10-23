using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

[Authorize]
public class PhimController : Controller
{
    private readonly ApplicationDbContext _db;
    public PhimController(ApplicationDbContext db) => _db = db;

    // ================= BM01 - Lịch chiếu phim (Direct Actor: Khách hàng) =================
    [HttpGet]
    public async Task<IActionResult> LichChieu(string? tuKhoa)
    {
        var query = _db.Phims.Where(p => !p.IsDeleted);
        if (!string.IsNullOrWhiteSpace(tuKhoa))
            query = query.Where(p => p.TenPhim.Contains(tuKhoa)); // TODO: đổi sang Full-Text Search khi deploy SQL Server thật

        ViewBag.TuKhoa = tuKhoa ?? "";
        var danhSach = await query.OrderByDescending(p => p.NgayTao).ToListAsync();
        return View(danhSach);
    }

    // ================= BM22 - Chi tiết phim (mới bổ sung, Direct Actor: Khách hàng) =================
    [HttpGet]
    public async Task<IActionResult> ChiTiet(string id)
    {
        var phim = await _db.Phims.FirstOrDefaultAsync(p => p.MaPhim == id && !p.IsDeleted);
        if (phim == null) return NotFound();

        var homNay = DateOnly.FromDateTime(DateTime.Now);
        var danhSachSuatChieu = await _db.LichChieus
            .Include(l => l.PhongChieu)
            .Where(l => l.MaPhim == id && l.NgayChieu >= homNay && l.TrangThai == TrangThaiLichChieu.DangChieu && !l.IsDeleted)
            .OrderBy(l => l.NgayChieu).ThenBy(l => l.GioBatDau)
            .ToListAsync();

        ViewBag.DanhSachSuatChieu = danhSachSuatChieu;
        return View(phim);
    }

    // ================= BM07 - Phiếu thêm phim mới (Direct Actor: Quản lý rạp / Admin) =================
    [HttpGet]
    [Authorize(Roles = "CinemaManager,Admin")]
    public IActionResult ThemPhim() => View();

    [HttpPost]
    [Authorize(Roles = "CinemaManager,Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ThemPhim(Phim phim)
    {
        // QD09: phim C18 không được xếp lịch chiếu trước 22:00 -> ràng buộc này kiểm tra ở bước
        // tạo LỊCH CHIẾU (LichChieuController), không phải ở bước thêm phim.
        phim.MaPhim = "PH" + Guid.NewGuid().ToString("N")[..8].ToUpper();
        phim.NgayTao = DateTime.Now;
        _db.Phims.Add(phim);
        await _db.SaveChangesAsync();

        TempData["ThongBao"] = $"Đã thêm phim \"{phim.TenPhim}\" thành công.";
        return RedirectToAction(nameof(LichChieu));
    }

    // ================= BM16 - Form tìm kiếm và lọc phim nâng cao (Direct Actor: Khách hàng) =================
    [HttpGet]
    public async Task<IActionResult> TimKiem(string? tuKhoa, string[]? theLoai, string? phanLoaiTuoi, string? sapXep)
    {
        var query = _db.Phims.Where(p => !p.IsDeleted);

        if (!string.IsNullOrWhiteSpace(tuKhoa))
            query = query.Where(p => p.TenPhim.Contains(tuKhoa) || (p.DaoDien != null && p.DaoDien.Contains(tuKhoa)));

        // Thể loại: checkbox chọn nhiều -> phim khớp BẤT KỲ thể loại nào được chọn
        if (theLoai != null && theLoai.Length > 0)
            query = query.Where(p => theLoai.Contains(p.TheLoai));

        // Phân loại tuổi: radio, "Tất cả" hoặc rỗng thì không lọc
        if (!string.IsNullOrWhiteSpace(phanLoaiTuoi) && phanLoaiTuoi != "TatCa"
            && Enum.TryParse<PhanLoaiTuoi>(phanLoaiTuoi, out var pl))
            query = query.Where(p => p.PhanLoaiTuoi == pl);

        // Sắp xếp theo
        query = sapXep switch
        {
            "NgayChieu" => query.OrderByDescending(p => p.NgayTao),
            "DanhGia" => query.OrderByDescending(p => p.DiemDanhGiaTb),
            _ => query.OrderByDescending(p => p.SoLuotDanhGia) // "PhoBien" mặc định
        };

        ViewBag.TuKhoa = tuKhoa ?? "";
        ViewBag.TheLoaiDaChon = theLoai ?? Array.Empty<string>();
        ViewBag.PhanLoaiTuoi = phanLoaiTuoi ?? "TatCa";
        ViewBag.SapXep = sapXep ?? "PhoBien";
        var ketQua = await query.ToListAsync();
        return View(ketQua);
    }
}
