using System.Security.Claims;
using CinemaBookingCS.Data;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

[Authorize(Roles = "CSKH,Admin,CinemaManager")]
public class CskhController : Controller
{
    private readonly ApplicationDbContext _db;
    public CskhController(ApplicationDbContext db) => _db = db;
    private string MaNguoiDungHienTai => User.FindFirst(ClaimTypes.NameIdentifier)!.Value;

    // ================= BM14 - Phiếu yêu cầu huỷ vé và hoàn tiền (Direct Actor: NV CSKH) =================
    [HttpGet]
    public async Task<IActionResult> TimDonHuy(string maDatVe)
    {
        if (string.IsNullOrWhiteSpace(maDatVe)) return View();

        var datVe = await _db.DatVes.Include(d => d.LichChieu)
            .FirstOrDefaultAsync(d => d.MaDatVe == maDatVe && !d.IsDeleted);
        if (datVe == null)
        {
            ViewBag.Loi = "Không tìm thấy đơn đặt vé.";
            return View();
        }

        // QD17: 10%(>24h) / 30%(2-24h) / 100%(<2h) - tính trước để hiển thị cho NV CSKH tham khảo
        var lichChieu = datVe.LichChieu!;
        var gioChieu = lichChieu.NgayChieu.ToDateTime(lichChieu.GioBatDau);
        var soGioConLai = (gioChieu - DateTime.Now).TotalHours;
        decimal tyLePhi = soGioConLai > 24 ? 0.10m : soGioConLai >= 2 ? 0.30m : 1.00m;

        ViewBag.TyLePhiGoiY = tyLePhi;
        return View(datVe);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> XuLyHuyVe(string maDatVe, string lyDo, PhuongThucHoanTien phuongThucHoan)
    {
        var datVe = await _db.DatVes.Include(d => d.LichChieu).FirstOrDefaultAsync(d => d.MaDatVe == maDatVe);
        if (datVe == null) return NotFound();

        var lichChieu = datVe.LichChieu!;
        var gioChieu = lichChieu.NgayChieu.ToDateTime(lichChieu.GioBatDau);
        var soGioConLai = (gioChieu - DateTime.Now).TotalHours;

        if (soGioConLai < 0)
        {
            TempData["ThongBao"] = "Suất chiếu đã qua, không thể huỷ vé.";
            return RedirectToAction(nameof(TimDonHuy), new { maDatVe });
        }

        decimal tyLePhi = soGioConLai > 24 ? 0.10m : soGioConLai >= 2 ? 0.30m : 1.00m; // QD17
        var phiHuy = Math.Round(datVe.TongTien * tyLePhi);
        var soTienHoan = datVe.TongTien - phiHuy;

        _db.HuyVes.Add(new HuyVe
        {
            MaHuyVe = "HV" + Guid.NewGuid().ToString("N")[..8].ToUpper(),
            MaDatVe = maDatVe,
            LyDo = lyDo,
            PhiHuy = phiHuy,
            SoTienHoan = soTienHoan,
            PhuongThucHoan = phuongThucHoan,
            NgayYeuCau = DateTime.Now,
            MaNvCskh = MaNguoiDungHienTai,
            TrangThai = TrangThaiHuyVe.DaHoanTien // TODO: nối luồng hoàn tiền thật qua cổng TT, hiện xử lý ngay
        });

        datVe.TrangThai = TrangThaiDatVe.DaHuy;

        // Giải phóng ghế: xoá chi tiết đặt vé để ghế lại "trống" cho suất chiếu này
        var chiTiet = await _db.ChiTietDatVes.Where(c => c.MaDatVe == maDatVe).ToListAsync();
        _db.ChiTietDatVes.RemoveRange(chiTiet);
        lichChieu.SoGheDaDat = Math.Max(0, lichChieu.SoGheDaDat - chiTiet.Count);

        await _db.SaveChangesAsync();
        TempData["ThongBao"] = $"Đã huỷ vé. Số tiền hoàn: {soTienHoan:N0} VNĐ (phí huỷ {phiHuy:N0} VNĐ).";
        return RedirectToAction(nameof(TimDonHuy), new { maDatVe });
    }

    // ================= BM15 - Phiếu yêu cầu đổi vé (Direct Actor: NV CSKH) =================
    [HttpGet]
    public async Task<IActionResult> TimDonDoi(string maDatVe)
    {
        if (string.IsNullOrWhiteSpace(maDatVe)) return View();

        var datVe = await _db.DatVes.Include(d => d.LichChieu).ThenInclude(l => l!.Phim)
            .FirstOrDefaultAsync(d => d.MaDatVe == maDatVe && !d.IsDeleted);
        if (datVe == null)
        {
            ViewBag.Loi = "Không tìm thấy đơn đặt vé.";
            return View();
        }

        ViewBag.CacSuatChieuKhac = await _db.LichChieus
            .Where(l => l.MaPhim == datVe.LichChieu!.MaPhim && l.MaLichChieu != datVe.MaLichChieu
                     && l.TrangThai == TrangThaiLichChieu.DangChieu && !l.IsDeleted)
            .ToListAsync();
        return View(datVe);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> XuLyDoiVe(string maDatVeGoc, string maLichChieuMoi, string maGheMoi)
    {
        var datVeGoc = await _db.DatVes.Include(d => d.LichChieu).Include(d => d.ChiTietDatVe)
            .FirstOrDefaultAsync(d => d.MaDatVe == maDatVeGoc);
        var lichChieuMoi = await _db.LichChieus.FindAsync(maLichChieuMoi);
        var gheMoi = await _db.Ghes.FindAsync(maGheMoi);
        if (datVeGoc == null || lichChieuMoi == null || gheMoi == null) return NotFound();

        var giaMoi = gheMoi.LoaiGhe == LoaiGhe.VIP ? lichChieuMoi.GiaVeVip : lichChieuMoi.GiaVeThuong;
        var chenhLech = giaMoi - (datVeGoc.TongTien / Math.Max(1, datVeGoc.ChiTietDatVe.Count));
        const decimal phiDoi = 10000; // QD18: phí đổi cố định 10.000 VNĐ

        try
        {
            _db.DoiVes.Add(new DoiVe
            {
                MaDoiVe = "DO" + Guid.NewGuid().ToString("N")[..8].ToUpper(),
                MaDatVeGoc = maDatVeGoc,
                MaLichChieuMoi = maLichChieuMoi,
                MaGheMoi = maGheMoi,
                ChenhLechGia = chenhLech,
                PhiDoi = phiDoi,
                MaNvCskh = MaNguoiDungHienTai,
                TrangThai = TrangThaiDoiVe.DaDoi
            });
            await _db.SaveChangesAsync();
            TempData["ThongBao"] = $"Đã đổi vé thành công. Chênh lệch giá: {chenhLech:N0} VNĐ, phí đổi: {phiDoi:N0} VNĐ.";
        }
        catch (DbUpdateException)
        {
            TempData["ThongBao"] = "Ghế mới vừa bị người khác đặt trước, vui lòng chọn ghế/suất chiếu khác.";
        }

        return RedirectToAction(nameof(TimDonDoi), new { maDatVe = maDatVeGoc });
    }
}
