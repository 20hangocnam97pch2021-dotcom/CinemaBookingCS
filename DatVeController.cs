using System.Security.Claims;
using CinemaBookingCS.Data;
using CinemaBookingCS.Helpers;
using CinemaBookingCS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CinemaBookingCS.Controllers;

// Đặt vé (BM02-BM06). Khách hàng tự đặt, HOẶC NV bán vé đặt hộ khách tại quầy (BM03 ghi rõ
// Actor "Khách hàng / NV bán vé"). Thanh toán VNPay/Momo THẬT: tạo yêu cầu thanh toán có ký
// số qua API thật của từng cổng, mã hoá thành ảnh QR và HIỂN THỊ NGAY TRÊN WEB CinemaBooking
// (không chuyển hướng sang trang khác) - khách quét bằng điện thoại để trả tiền. Trang QR tự
// hỏi lại server (polling) để phát hiện đã thanh toán xong. Gửi email vé QR thật qua Gmail
// SMTP sau khi thanh toán thành công (QD06). Nếu chưa cấu hình khoá (appsettings.json rỗng)
// thì tự động chuyển sang giả lập, không chặn luồng demo.
[Authorize]
public class DatVeController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IEmailService _email;
    private readonly VNPayService _vnpay;
    private readonly MomoService _momo;
    public DatVeController(ApplicationDbContext db, IEmailService email, VNPayService vnpay, MomoService momo)
    {
        _db = db; _email = email; _vnpay = vnpay; _momo = momo;
    }

    private string MaNguoiDungHienTai => User.FindFirst(ClaimTypes.NameIdentifier)!.Value;
    private string? LoaiTaiKhoanHienTai => User.FindFirst("LoaiTaiKhoan")?.Value;
    private bool LaKhachHang => LoaiTaiKhoanHienTai == "Khách hàng";
    private bool LaNhanVienBanVe => LoaiTaiKhoanHienTai == "NhanVien"
        && (User.IsInRole("BoxOffice") || User.IsInRole("Admin") || User.IsInRole("CinemaManager"));

    /// <summary>true nếu người dùng hiện tại được phép đặt vé (khách tự đặt, hoặc NV bán vé đặt hộ).</summary>
    private bool DuocPhepDatVe => LaKhachHang || LaNhanVienBanVe;

    // HOLD_SEAT_MINUTES (tham số hệ thống, mặc định 20 phút - QD04). TODO: đọc động từ bảng
    // CauHinhHeThong (mã HOLD_SEAT_MINUTES) thay vì hằng số, để Admin sửa được qua BM19.
    private const int PhutGiuGheTam = 20;

    /// <summary>
    /// UC04 (Khoá ghế tạm thời): giải phóng các đơn ĐANG "Chờ thanh toán" nhưng đã quá
    /// PhutGiuGheTam mà chưa thanh toán — đúng theo State Diagram (Chờ thanh toán → Hết hạn).
    /// Gọi hàm này ở đầu mỗi bước liên quan tới ghế để đảm bảo dữ liệu luôn mới nhất
    /// (cách đơn giản, không cần background job/queue cho quy mô đồ án).
    /// </summary>
    private async Task GiaiPhongGheHetHanAsync(string maLichChieu)
    {
        var hanGiu = DateTime.Now.AddMinutes(-PhutGiuGheTam);
        var donHetHan = await _db.DatVes
            .Where(d => d.MaLichChieu == maLichChieu && d.TrangThai == TrangThaiDatVe.ChoThanhToan && d.NgayDat < hanGiu)
            .ToListAsync();
        if (donHetHan.Count == 0) return;

        foreach (var don in donHetHan)
        {
            var chiTiet = await _db.ChiTietDatVes.Where(c => c.MaDatVe == don.MaDatVe).ToListAsync();
            _db.ChiTietDatVes.RemoveRange(chiTiet); // nhả ghế về FREE
            don.TrangThai = TrangThaiDatVe.HetHan;
        }
        await _db.SaveChangesAsync();
    }

    // ================= BM02 - Sơ đồ ghế ngồi (Direct Actor: Khách hàng) =================
    // Trạng thái ghế TÍNH ĐỘNG theo suất chiếu, dựa trên ChiTietDatVe (đơn Chờ TT = HOLD, Đã TT = BOOKED).
    [HttpGet]
    public async Task<IActionResult> SoDoGhe(string maLichChieu)
    {
        await GiaiPhongGheHetHanAsync(maLichChieu);

        var lichChieu = await _db.LichChieus
            .Include(l => l.Phim).Include(l => l.PhongChieu)
            .FirstOrDefaultAsync(l => l.MaLichChieu == maLichChieu);
        if (lichChieu == null) return NotFound();

        var danhSachGhe = await _db.Ghes
            .Where(g => g.MaPhong == lichChieu.MaPhong)
            .OrderBy(g => g.HangGhe).ThenBy(g => g.SoGhe)
            .ToListAsync();

        // Ghế đang HOLD hoặc đã BOOKED cho ĐÚNG suất chiếu này (đã lọc hết hạn ở trên)
        var maGheDaGiu = await _db.ChiTietDatVes
            .Where(c => c.MaLichChieu == maLichChieu && c.DatVe!.TrangThai != TrangThaiDatVe.DaHuy && c.DatVe.TrangThai != TrangThaiDatVe.HetHan)
            .Select(c => c.MaGhe)
            .ToListAsync();

        ViewBag.LichChieu = lichChieu;
        ViewBag.MaGheDaDat = maGheDaGiu.ToHashSet();
        return View(danhSachGhe);
    }

    // ================= BM03 - UC03 chọn ghế + UC04 khoá ghế tạm thời =================
    // Đây là bước TẠO HOLD THẬT (ghi DatVe + ChiTietDatVe với trạng thái Chờ thanh toán),
    // KHÔNG phải chỉ hiển thị form. Từ lúc này ghế đã chuyển FREE -> HOLD cho người khác thấy.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> XacNhanGhe(string maLichChieu, string[] maGheDaChon, string? sdtKhachHang)
    {
        if (!DuocPhepDatVe)
        {
            TempData["ThongBao"] = "Chỉ Khách hàng hoặc Nhân viên bán vé mới đặt vé được.";
            return RedirectToAction("LichChieu", "Phim");
        }

        if (maGheDaChon == null || maGheDaChon.Length == 0)
            return RedirectToAction(nameof(SoDoGhe), new { maLichChieu });

        if (maGheDaChon.Length > 8) // QD03: tối đa 8 ghế/lần đặt
        {
            TempData["ThongBao"] = "Chỉ được chọn tối đa 8 ghế mỗi lần đặt.";
            return RedirectToAction(nameof(SoDoGhe), new { maLichChieu });
        }

        // Xác định khách hàng của đơn: khách tự đặt -> chính họ; NV bán vé -> tra cứu theo SĐT
        string maKhCuaDon;
        string? maNvBanVe = null;
        if (LaKhachHang)
        {
            maKhCuaDon = MaNguoiDungHienTai;
        }
        else // NV bán vé đặt hộ - phải nhập SĐT khách để tra cứu
        {
            if (string.IsNullOrWhiteSpace(sdtKhachHang))
            {
                TempData["ThongBao"] = "Vui lòng nhập số điện thoại khách hàng để đặt vé hộ.";
                return RedirectToAction(nameof(SoDoGhe), new { maLichChieu });
            }
            var khach = await _db.KhachHangs.FirstOrDefaultAsync(k => k.Sdt == sdtKhachHang.Trim() && !k.IsDeleted);
            if (khach == null)
            {
                TempData["ThongBao"] = $"Không tìm thấy khách hàng với số điện thoại {sdtKhachHang}. Khách cần đăng ký tài khoản trước.";
                return RedirectToAction(nameof(SoDoGhe), new { maLichChieu });
            }
            maKhCuaDon = khach.MaKh;
            maNvBanVe = MaNguoiDungHienTai; // ghi nhận NV nào bán
        }

        await GiaiPhongGheHetHanAsync(maLichChieu);

        var lichChieu = await _db.LichChieus.Include(l => l.Phim).Include(l => l.PhongChieu)
            .FirstOrDefaultAsync(l => l.MaLichChieu == maLichChieu);
        if (lichChieu == null) return NotFound();

        var ghes = await _db.Ghes.Where(g => maGheDaChon.Contains(g.MaGhe)).ToListAsync();

        await using var giaoDich = await _db.Database.BeginTransactionAsync();
        try
        {
            var maDatVe = "DV" + Guid.NewGuid().ToString("N")[..10].ToUpper();
            decimal tongTien = 0;

            var datVe = new DatVe
            {
                MaDatVe = maDatVe,
                MaKh = maKhCuaDon,
                MaNv = maNvBanVe, // null nếu khách tự đặt, có giá trị nếu NV bán vé đặt hộ
                MaLichChieu = maLichChieu,
                NgayDat = DateTime.Now, // mốc thời gian dùng để tính hết hạn hold (QD04)
                TrangThai = TrangThaiDatVe.ChoThanhToan,
                TenPhimSnapshot = lichChieu.Phim!.TenPhim,
                TenPhongSnapshot = lichChieu.PhongChieu!.TenPhong,
                NgayChieuSnapshot = lichChieu.NgayChieu,
                GioChieuSnapshot = lichChieu.GioBatDau,
                TongTien = 0
            };
            _db.DatVes.Add(datVe);

            foreach (var ghe in ghes)
            {
                var gia = ghe.LoaiGhe == LoaiGhe.VIP ? lichChieu.GiaVeVip : lichChieu.GiaVeThuong;
                tongTien += gia;
                _db.ChiTietDatVes.Add(new ChiTietDatVe
                {
                    MaChiTiet = "CT" + Guid.NewGuid().ToString("N")[..10].ToUpper(),
                    MaDatVe = maDatVe,
                    MaGhe = ghe.MaGhe,
                    MaLichChieu = maLichChieu,
                    GiaVe = gia
                });
            }
            datVe.TongTien = tongTien;

            // Đây là chỗ CHẶN TRÙNG GHẾ THẬT (UC04): nếu ghế đã bị người khác giữ/hold cho
            // cùng suất chiếu, unique index (MaLichChieu, MaGhe) sẽ ném lỗi ngay tại bước này.
            await _db.SaveChangesAsync();
            await giaoDich.CommitAsync();

            ViewBag.LichChieu = lichChieu;
            ViewBag.MaDatVe = maDatVe;
            ViewBag.HetHanLuc = datVe.NgayDat.AddMinutes(PhutGiuGheTam);
            return View("PhieuDatVe", ghes);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException sql && (sql.Number == 2601 || sql.Number == 2627))
        {
            await giaoDich.RollbackAsync();
            TempData["ThongBao"] = "Một hoặc nhiều ghế vừa được người khác giữ/đặt trước, vui lòng chọn lại ghế khác.";
            return RedirectToAction(nameof(SoDoGhe), new { maLichChieu });
        }
    }

    // ================= BM04 - UC05 Thanh toán vé xem phim =================
    // Chỉ CẬP NHẬT đơn đã hold ở bước trên (không tạo DatVe/ChiTietDatVe mới).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ThanhToan(string maDatVe, PhuongThucThanhToan phuongThucTt)
    {
        if (!DuocPhepDatVe)
        {
            TempData["ThongBao"] = "Không có quyền thực hiện thanh toán.";
            return RedirectToAction("LichChieu", "Phim");
        }

        var datVe = await _db.DatVes
            .Include(d => d.ChiTietDatVe).ThenInclude(c => c.Ghe)
            .Include(d => d.LichChieu)
            .FirstOrDefaultAsync(d => d.MaDatVe == maDatVe);
        if (datVe == null) return NotFound();

        // Khách hàng chỉ được thanh toán đơn của chính mình
        if (LaKhachHang && datVe.MaKh != MaNguoiDungHienTai) return Forbid();

        // QD05: thanh toán online phải hoàn tất trong 10 phút, TRÙNG VỚI thời gian hold 20 phút
        var quaHanHold = DateTime.Now > datVe.NgayDat.AddMinutes(PhutGiuGheTam);
        var quaHanThanhToanOnline = phuongThucTt != PhuongThucThanhToan.TienMat
            && DateTime.Now > datVe.NgayDat.AddMinutes(10); // PAYMENT_TIMEOUT_MINUTES

        if (datVe.TrangThai != TrangThaiDatVe.ChoThanhToan || quaHanHold || quaHanThanhToanOnline)
        {
            if (datVe.TrangThai == TrangThaiDatVe.ChoThanhToan && quaHanHold)
            {
                datVe.TrangThai = TrangThaiDatVe.HetHan;
                _db.ChiTietDatVes.RemoveRange(datVe.ChiTietDatVe);
                await _db.SaveChangesAsync();
            }
            TempData["ThongBao"] = "Đã hết thời gian giữ ghế hoặc thời gian thanh toán, vui lòng đặt lại.";
            return RedirectToAction(nameof(SoDoGhe), new { maLichChieu = datVe.MaLichChieu });
        }

        // Lưu phương thức đã chọn để dùng khi trang QR hỏi lại trạng thái (polling)
        TempData["PhuongThucTt_" + maDatVe] = phuongThucTt.ToString();
        TempData.Keep();

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

        // === VNPay thật: đã cấu hình khoá -> tạo URL thanh toán thật (có ký HMAC-SHA512),
        // mã hoá URL đó thành ảnh QR và HIỂN THỊ NGAY TRÊN WEB (không chuyển hướng đi nơi khác).
        // Khách dùng camera điện thoại quét mã, mở trang thanh toán VNPay trên điện thoại để trả tiền.
        if (phuongThucTt == PhuongThucThanhToan.VNPay && _vnpay.DaCauHinh)
        {
            var urlThanhToan = _vnpay.TaoUrlThanhToan(maDatVe, datVe.TongTien, ip);
            ViewBag.QrBase64 = QrHelper.TaoQrBase64(urlThanhToan);
            ViewBag.LinkThanhToan = urlThanhToan; // để khách bấm trực tiếp nếu quét QR không tiện
            ViewBag.MaDatVe = maDatVe;
            ViewBag.TongTien = datVe.TongTien;
            ViewBag.PhuongThuc = "VNPay";
            ViewBag.HetHanLuc = datVe.NgayDat.AddMinutes(10); // PAYMENT_TIMEOUT_MINUTES (QD05)
            return View("ThanhToanQr");
        }

        // === Momo thật: gọi API Momo lấy payUrl thật, mã hoá thành QR hiện trên web tương tự ===
        if (phuongThucTt == PhuongThucThanhToan.Momo && _momo.DaCauHinh)
        {
            var payUrl = await _momo.TaoUrlThanhToanAsync(maDatVe, datVe.TongTien);
            if (string.IsNullOrEmpty(payUrl))
            {
                TempData["ThongBao"] = "Không kết nối được cổng Momo, vui lòng thử lại.";
                return RedirectToAction(nameof(SoDoGhe), new { maLichChieu = datVe.MaLichChieu });
            }
            ViewBag.QrBase64 = QrHelper.TaoQrBase64(payUrl);
            ViewBag.LinkThanhToan = payUrl;
            ViewBag.MaDatVe = maDatVe;
            ViewBag.TongTien = datVe.TongTien;
            ViewBag.PhuongThuc = "Momo";
            ViewBag.HetHanLuc = datVe.NgayDat.AddMinutes(10);
            return View("ThanhToanQr");
        }

        // === Còn lại (Tiền mặt tại quầy, Thẻ ngân hàng, hoặc cổng chưa cấu hình khoá): giả lập thành công ===
        var danhSachVe = await HoanTatThanhToanAsync(datVe, phuongThucTt, "GIALAP-" + Guid.NewGuid().ToString("N")[..12].ToUpper());
        return View("XacNhanVe", danhSachVe);
    }

    // ================= Trang QR chờ thanh toán tự hỏi lại trạng thái (polling) =================
    // Khách quét QR bằng ĐIỆN THOẠI (thiết bị khác với máy đang mở trang này), nên kết quả
    // trả về (VNPayCallback/MomoCallback) sẽ chạy trên trình duyệt điện thoại, không phải máy
    // này. Trang này tự hỏi lại server mỗi 3 giây xem đơn đã được thanh toán chưa.
    [HttpGet]
    public async Task<IActionResult> KiemTraTrangThai(string maDatVe)
    {
        var datVe = await _db.DatVes.FirstOrDefaultAsync(d => d.MaDatVe == maDatVe);
        if (datVe == null) return NotFound();
        return Json(new { daThanhToan = datVe.TrangThai == TrangThaiDatVe.DaThanhToan });
    }

    // Sau khi trang QR phát hiện đã thanh toán xong (qua polling), chuyển tới đây để xem vé
    [HttpGet]
    public async Task<IActionResult> XemVeSauThanhToan(string maDatVe)
    {
        var datVe = await _db.DatVes.FirstOrDefaultAsync(d => d.MaDatVe == maDatVe);
        if (datVe == null || datVe.TrangThai != TrangThaiDatVe.DaThanhToan)
            return RedirectToAction("LichChieu", "Phim");

        var danhSachVe = await _db.Ves.Where(v => v.MaDatVe == maDatVe).ToListAsync();
        return View("XacNhanVe", danhSachVe);
    }

    // ================= VNPay trả kết quả về =================
    [HttpGet]
    public async Task<IActionResult> VNPayCallback()
    {
        var maDatVe = Request.Query["vnp_TxnRef"].ToString();
        var datVe = await LayDonChoThanhToanAsync(maDatVe);
        if (datVe == null) { TempData["ThongBao"] = "Không tìm thấy đơn đặt vé."; return RedirectToAction("LichChieu", "Phim"); }

        if (_vnpay.XacThucCallback(Request.Query))
        {
            var maGd = Request.Query["vnp_TransactionNo"].ToString();
            var danhSachVe = await HoanTatThanhToanAsync(datVe, PhuongThucThanhToan.VNPay, maGd);
            return View("XacNhanVe", danhSachVe);
        }
        return await HuyThanhToanThatBai(datVe, "VNPay báo giao dịch không thành công.");
    }

    // ================= Momo trả kết quả về =================
    [HttpGet]
    public async Task<IActionResult> MomoCallback()
    {
        var orderId = Request.Query["orderId"].ToString();
        var maDatVe = orderId.Contains('_') ? orderId[..orderId.IndexOf('_')] : orderId;
        var datVe = await LayDonChoThanhToanAsync(maDatVe);
        if (datVe == null) { TempData["ThongBao"] = "Không tìm thấy đơn đặt vé."; return RedirectToAction("LichChieu", "Phim"); }

        if (_momo.XacThucCallback(Request.Query))
        {
            var maGd = Request.Query["transId"].ToString();
            var danhSachVe = await HoanTatThanhToanAsync(datVe, PhuongThucThanhToan.Momo, maGd);
            return View("XacNhanVe", danhSachVe);
        }
        return await HuyThanhToanThatBai(datVe, "Momo báo giao dịch không thành công.");
    }

    private async Task<DatVe?> LayDonChoThanhToanAsync(string maDatVe) =>
        await _db.DatVes.Include(d => d.ChiTietDatVe).ThenInclude(c => c.Ghe).Include(d => d.LichChieu)
            .FirstOrDefaultAsync(d => d.MaDatVe == maDatVe);

    private async Task<IActionResult> HuyThanhToanThatBai(DatVe datVe, string thongBao)
    {
        // Giao dịch thất bại: nhả ghế, cho đặt lại
        if (datVe.TrangThai == TrangThaiDatVe.ChoThanhToan)
        {
            datVe.TrangThai = TrangThaiDatVe.HetHan;
            var ct = await _db.ChiTietDatVes.Where(c => c.MaDatVe == datVe.MaDatVe).ToListAsync();
            _db.ChiTietDatVes.RemoveRange(ct);
            await _db.SaveChangesAsync();
        }
        TempData["ThongBao"] = thongBao + " Vui lòng đặt lại.";
        return RedirectToAction(nameof(SoDoGhe), new { maLichChieu = datVe.MaLichChieu });
    }

    /// <summary>
    /// Hàm dùng chung: ghi nhận thanh toán thành công (UC05) -> sinh vé QR -> cập nhật trạng thái
    /// -> gửi email vé (QD06). Dùng cho cả 3 đường: giả lập, VNPay callback, Momo callback.
    /// </summary>
    private async Task<List<Ve>> HoanTatThanhToanAsync(DatVe datVe, PhuongThucThanhToan phuongThuc, string maGiaoDich)
    {
        if (datVe.TrangThai == TrangThaiDatVe.DaThanhToan)
            return await _db.Ves.Where(v => v.MaDatVe == datVe.MaDatVe).ToListAsync(); // đã xử lý rồi, tránh sinh vé 2 lần

        _db.ThanhToans.Add(new ThanhToan
        {
            MaThanhToan = "TT" + Guid.NewGuid().ToString("N")[..10].ToUpper(),
            MaDatVe = datVe.MaDatVe,
            MaGiaoDich = maGiaoDich,
            PhuongThuc = phuongThuc,
            ThoiGianTt = DateTime.Now,
            SoTien = datVe.TongTien,
            TrangThai = TrangThaiThanhToan.ThanhCong
        });
        datVe.TrangThai = TrangThaiDatVe.DaThanhToan;

        var danhSachVe = new List<Ve>();
        foreach (var chiTiet in datVe.ChiTietDatVe)
        {
            var ve = new Ve
            {
                MaVe = "VE" + Guid.NewGuid().ToString("N")[..10].ToUpper(),
                MaDatVe = datVe.MaDatVe,
                MaGhe = chiTiet.MaGhe,
                MaQr = Guid.NewGuid().ToString("N"),
                TrangThai = TrangThaiVe.HopLe,
                TenPhimSnapshot = datVe.TenPhimSnapshot,
                SoGheSnapshot = $"{chiTiet.Ghe!.HangGhe}{chiTiet.Ghe.SoGhe}",
                SuatChieuSnapshot = $"{datVe.NgayChieuSnapshot:dd/MM/yyyy} {datVe.GioChieuSnapshot:HH:mm}"
            };
            _db.Ves.Add(ve);
            danhSachVe.Add(ve);
        }

        datVe.LichChieu!.SoGheDaDat += datVe.ChiTietDatVe.Count;
        var kh = await _db.KhachHangs.FindAsync(datVe.MaKh);
        if (kh != null) { kh.TongSoVeDaDat += datVe.ChiTietDatVe.Count; kh.TongChiTieu += datVe.TongTien; }

        await _db.SaveChangesAsync();

        // QD06: gửi email vé QR cho khách (nếu đã cấu hình SMTP). Không để lỗi mail làm hỏng luồng.
        if (kh != null)
        {
            try { await GuiEmailVeAsync(kh.Email, kh.HoTen, datVe, danhSachVe); }
            catch (Exception) { /* đã log trong EmailService; không chặn luồng đặt vé */ }
        }

        return danhSachVe;
    }

    private async Task GuiEmailVeAsync(string emailKhach, string tenKhach, DatVe datVe, List<Ve> ves)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"<h2>Đặt vé thành công — CinemaBooking</h2>");
        sb.Append($"<p>Chào {tenKhach}, cảm ơn bạn đã đặt vé. Mã đơn: <b>{datVe.MaDatVe}</b></p>");
        sb.Append($"<p>Phim: <b>{datVe.TenPhimSnapshot}</b><br/>Suất chiếu: {datVe.NgayChieuSnapshot:dd/MM/yyyy} {datVe.GioChieuSnapshot:HH:mm}<br/>Phòng: {datVe.TenPhongSnapshot}</p>");
        sb.Append("<p>Vé của bạn (xuất trình mã QR tại quầy soát vé):</p>");
        foreach (var ve in ves)
        {
            var qr = Helpers.QrHelper.TaoQrBase64(ve.MaQr);
            sb.Append($"<div style='display:inline-block;text-align:center;margin:8px'><img src='{qr}' width='140'/><br/>Ghế {ve.SoGheSnapshot}</div>");
        }
        sb.Append($"<p>Tổng tiền: <b>{datVe.TongTien:N0} VNĐ</b></p>");
        await _email.GuiAsync(emailKhach, "Vé xem phim của bạn — CinemaBooking", sb.ToString());
    }

    // ================= BM06 - Lịch sử đặt vé (Direct Actor: Khách hàng) =================
    [HttpGet]
    public async Task<IActionResult> LichSu(DateOnly? tuNgay, DateOnly? denNgay, TrangThaiDatVe? trangThai)
    {
        var query = _db.DatVes.Where(d => d.MaKh == MaNguoiDungHienTai && !d.IsDeleted && d.TrangThai != TrangThaiDatVe.HetHan);

        if (tuNgay.HasValue) query = query.Where(d => d.NgayChieuSnapshot >= tuNgay.Value);
        if (denNgay.HasValue) query = query.Where(d => d.NgayChieuSnapshot <= denNgay.Value);
        if (trangThai.HasValue) query = query.Where(d => d.TrangThai == trangThai.Value);

        ViewBag.TuNgay = tuNgay;
        ViewBag.DenNgay = denNgay;
        ViewBag.TrangThai = trangThai;

        var ketQua = await query.OrderByDescending(d => d.NgayDat).ToListAsync();
        return View(ketQua);
    }
    // Ví dụ trong DatVeController.cs
[HttpGet]
public async Task<IActionResult> ChiTietVe(string maDatVe)
{
    if (string.IsNullOrEmpty(maDatVe))
    {
        return NotFound();
    }

    // 1. Truy vấn DB bằng Entity Framework để lấy thông tin đơn đặt vé và danh sách ghế
    var donDatVe = await _db.DatVes
        .Include(d => d.ChiTietDatVe) 
        .Include(d => d.LichChieu)       // Gọi đến bảng trung gian là Lịch chiếu
            .ThenInclude(l => l.Phim)    // Từ Lịch chiếu gọi tiếp đến Phim
        .FirstOrDefaultAsync(d => d.MaDatVe == maDatVe);

    if (donDatVe == null)
    {
        return NotFound();
    }

    // 2. Trả dữ liệu về cho View
    return View(donDatVe);
}
}
